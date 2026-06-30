using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BackupService.Connections.Usb;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Watches for USB drive connect/disconnect (a hidden top-level window receiving <c>WM_DEVICECHANGE</c> volume
    /// broadcasts on a dedicated message-loop thread, mirroring <c>WindowsTrayService</c>). On connect it identifies
    /// the arriving drive, matches it against the registered USB connections (<see cref="UsbDeviceMatcher"/>), logs +
    /// notifies, and runs any enabled FolderPair/ArchiveSync profile that uses the connection as a source. On
    /// disconnect it logs + notifies. Windows-only; registered as a singleton + hosted service.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class UsbDeviceWatcherService(
        IDatabaseContextFactory contextFactory,
        IUsbDeviceInspector inspector,
        IMtpDeviceInspector mtpInspector,
        IBackupRunner backupRunner,
        IOperationLogFactory operationLogFactory,
        IDesktopNotifier notifier,
        ILogger<UsbDeviceWatcherService> logger) : IHostedService
    {
        private readonly object _gate = new();
        // Drive letter -> the connections it matched on arrival, so a later removal can be logged (the device is
        // gone by then and can't be re-read).
        private readonly Dictionary<string, List<MatchedConnection>> _matched = new(StringComparer.OrdinalIgnoreCase);
        // Debounce duplicate arrival broadcasts for the same drive.
        private readonly Dictionary<string, DateTime> _lastArrival = new(StringComparer.OrdinalIgnoreCase);

        // MTP devices raise no volume event; instead we re-scan portable devices on a device-tree change and diff
        // against this snapshot to spot arrivals/removals. Seeded at start so already-attached devices aren't "new".
        private readonly HashSet<string> _knownMtpSerials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<MatchedConnection>> _matchedMtp = new(StringComparer.OrdinalIgnoreCase);
        private int _mtpScanInFlight; // 0/1 — coalesces a burst of device-tree changes into one re-scan sequence

        private Thread? _thread;
        private IntPtr _hwnd;
        private WndProcDelegate? _wndProc; // kept alive so the native callback isn't collected

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Task.CompletedTask;
            }

            // Seed the MTP snapshot with already-connected devices so they don't count as arrivals at startup.
            // (A device already attached when the service starts is therefore not auto-run — replug it to trigger.)
            _ = Task.Run(() =>
            {
                try
                {
                    var devices = mtpInspector.EnumerateMtpDevices();
                    lock (_gate)
                    {
                        foreach (var device in devices)
                        {
                            _knownMtpSerials.Add(device.Serial);
                        }
                    }
                    logger.LogInformation("USB watcher: seeded {Count} already-connected portable (MTP) device(s): {Devices}",
                        devices.Count, DescribeDevices(devices));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to seed the MTP device snapshot.");
                }
            });

            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "BackupService.UsbWatcher",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            _thread?.Join(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        private void MessageLoop()
        {
            try
            {
                _wndProc = WndProc;

                const string className = "BackupServiceUsbWatcherWindow";
                var wndClass = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className,
                };
                RegisterClass(ref wndClass);

                // A normal (never-shown) top-level window — WM_DEVICECHANGE volume broadcasts don't reach
                // message-only windows.
                _hwnd = CreateWindowEx(0, className, "Backup Service USB Watcher", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    logger.LogError("Failed to create the USB watcher window (Win32 error {Error}).", Marshal.GetLastWin32Error());
                    return;
                }

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The USB watcher message loop ended unexpectedly.");
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_APP_QUIT)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            // A device-tree change (no volume payload) — re-scan portable (MTP) devices. Fires broadly, so debounce.
            if (msg == WM_DEVICECHANGE && (uint)wParam == DBT_DEVNODES_CHANGED)
            {
                OnDeviceNodesChanged();
            }

            if (msg == WM_DEVICECHANGE && lParam != IntPtr.Zero
                && (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE))
            {
                var header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (header.dbch_devicetype == DBT_DEVTYP_VOLUME)
                {
                    var volume = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
                    var letters = UsbDriveLetters.FromUnitMask(volume.dbcv_unitmask);
                    if (wParam == DBT_DEVICEARRIVAL)
                    {
                        OnArrived(letters);
                    }
                    else
                    {
                        OnRemoved(letters);
                    }
                }
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void OnArrived(IReadOnlyList<string> driveLetters)
        {
            foreach (var letter in driveLetters)
            {
                lock (_gate)
                {
                    // Skip a duplicate arrival broadcast for the same drive within a short window.
                    if (_lastArrival.TryGetValue(letter, out var last) && (DateTime.UtcNow - last) < TimeSpan.FromSeconds(3))
                    {
                        continue;
                    }
                    _lastArrival[letter] = DateTime.UtcNow;
                }

                // Read identity now while the drive is present; the DB work + runs happen off the loop thread.
                var device = inspector.Inspect(letter);
                if (device is not null)
                {
                    _ = Task.Run(() => HandleArrivalAsync(letter, device));
                }
            }
        }

        private void OnRemoved(IReadOnlyList<string> driveLetters)
        {
            foreach (var letter in driveLetters)
            {
                _ = Task.Run(() => HandleRemovalAsync(letter));
            }
        }

        private async Task HandleArrivalAsync(string driveLetter, UsbDevice device)
        {
            try
            {
                await using var db = contextFactory.CreateDbContext();

                var usbConnections = await db.Connections
                    .AsNoTracking()
                    .Where(c => c.Type == ConnectionType.Usb)
                    .Include(c => c.Usb)
                    .ToListAsync();

                var matched = new List<MatchedConnection>();
                foreach (var connection in usbConnections)
                {
                    if (connection.Usb is not { Kind: UsbDeviceKind.MassStorage } usb)
                    {
                        continue;
                    }

                    var info = new UsbConnectionInfo(usb.Kind, usb.HardwareSerial, usb.VolumeSerial, usb.MtpSerial, usb.RootFolder);
                    if (UsbDeviceMatcher.Matches(info, device))
                    {
                        matched.Add(new MatchedConnection(connection.Id, connection.Name,
                            usb.NotificationsEnabled, usb.NotifyOnConnect, usb.NotifyOnDisconnect));
                    }
                }

                if (matched.Count == 0)
                {
                    return;
                }

                lock (_gate)
                {
                    _matched[driveLetter] = matched;
                }

                await HandleMatchedAsync(db, matched);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle USB arrival for {Drive}.", driveLetter);
            }
        }

        // Shared "matched connection(s) arrived" path: log + notify, then run the enabled FolderPair/ArchiveSync
        // profiles whose source is one of them. Used by both the mass-storage (volume) and MTP arrival paths.
        private async Task HandleMatchedAsync(Database.BackupDbContext db, List<MatchedConnection> matched)
        {
            foreach (var connection in matched)
            {
                await operationLogFactory.CreateAsync($"USB device '{connection.Name}' connected");
                if (connection.NotificationsEnabled && connection.NotifyOnConnect)
                {
                    notifier.NotifyDeviceConnected(connection.Name);
                }
            }

            var connectionIds = matched.Select(m => m.ConnectionId).ToList();
            var profileIds = await db.Profiles
                .AsNoTracking()
                .Where(p => p.Enabled
                    && (p.Type == ProfileType.FolderPair || p.Type == ProfileType.ArchiveSync)
                    && p.SourceConnectionId != null && connectionIds.Contains(p.SourceConnectionId.Value))
                .Select(p => p.Id)
                .ToListAsync();

            foreach (var profileId in profileIds)
            {
                logger.LogInformation("USB device connected — running profile {ProfileId}.", profileId);
                _ = Task.Run(() => backupRunner.RunAsync(profileId, manual: false));
            }
        }

        // ---- MTP (portable-device) detection ----

        private void OnDeviceNodesChanged()
        {
            // A device-tree change usually comes as a burst; coalesce into a single re-scan sequence. The sequence
            // itself re-scans several times, so we ignore further changes until it finishes.
            if (Interlocked.CompareExchange(ref _mtpScanInFlight, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(ScanMtpSequenceAsync);
        }

        // A portable device (especially a camera that prompts on-screen for a USB mode) can take several seconds
        // to register with WPD after the device-tree change fires. Scan a few times over ~8s so a late arrival is
        // still caught — the diff against _knownMtpSerials keeps the repeats idempotent (each device fires once).
        private async Task ScanMtpSequenceAsync()
        {
            try
            {
                logger.LogDebug("USB watcher: device-tree changed — scanning for portable (MTP) devices.");
                int[] delaysMs = [0, 1500, 3000, 5000, 8000];
                foreach (var delay in delaysMs)
                {
                    if (delay > 0)
                    {
                        await Task.Delay(delay);
                    }
                    await ScanMtpAsync();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _mtpScanInFlight, 0);
            }
        }

        private async Task ScanMtpAsync()
        {
            try
            {
                var present = mtpInspector.EnumerateMtpDevices();
                logger.LogDebug("USB watcher: MTP scan found {Count} portable device(s): {Devices}",
                    present.Count, DescribeDevices(present));

                List<MtpDevice> arrived;
                List<string> removed;
                lock (_gate)
                {
                    var presentSerials = present.Select(d => d.Serial).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    arrived = present.Where(d => !_knownMtpSerials.Contains(d.Serial)).ToList();
                    removed = _knownMtpSerials.Where(s => !presentSerials.Contains(s)).ToList();
                    _knownMtpSerials.Clear();
                    foreach (var serial in presentSerials)
                    {
                        _knownMtpSerials.Add(serial);
                    }
                }

                foreach (var device in arrived)
                {
                    logger.LogInformation("USB watcher: portable (MTP) device connected: '{Name}' ({Serial}).", device.Name, device.Serial);
                    await HandleMtpArrivalAsync(device.Serial, device.Name);
                }
                foreach (var serial in removed)
                {
                    logger.LogInformation("USB watcher: portable (MTP) device disconnected ({Serial}).", serial);
                    await HandleMtpRemovalAsync(serial);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan MTP devices.");
            }
        }

        private async Task HandleMtpArrivalAsync(string serial, string name)
        {
            try
            {
                await using var db = contextFactory.CreateDbContext();

                var mtpConnections = await db.Connections
                    .AsNoTracking()
                    .Where(c => c.Type == ConnectionType.Usb)
                    .Include(c => c.Usb)
                    .ToListAsync();

                var mtpRegistrations = mtpConnections
                    .Where(c => c.Usb is { Kind: UsbDeviceKind.Mtp })
                    .ToList();

                var matched = new List<MatchedConnection>();
                foreach (var connection in mtpRegistrations)
                {
                    if (UsbDeviceMatcher.MatchesMtp(connection.Usb!.MtpSerial, serial))
                    {
                        matched.Add(new MatchedConnection(connection.Id, connection.Name,
                            connection.Usb!.NotificationsEnabled, connection.Usb!.NotifyOnConnect, connection.Usb!.NotifyOnDisconnect));
                    }
                }

                if (matched.Count == 0)
                {
                    // Visible so a serial mismatch (e.g. an unstable WPD DeviceId) is diagnosable — it shows the
                    // arrived id next to every registered MTP connection's stored id.
                    var registered = mtpRegistrations.Count == 0
                        ? "(no MTP connections configured)"
                        : string.Join("; ", mtpRegistrations.Select(c => $"'{c.Name}' [{c.Usb!.MtpSerial}]"));
                    logger.LogInformation(
                        "USB watcher: portable device '{Name}' ({Serial}) matched no MTP connection. Registered: {Registered}",
                        name, serial, registered);
                    return;
                }

                lock (_gate)
                {
                    _matchedMtp[serial] = matched;
                }

                await HandleMatchedAsync(db, matched);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle MTP arrival.");
            }
        }

        private async Task HandleMtpRemovalAsync(string serial)
        {
            List<MatchedConnection>? matched;
            lock (_gate)
            {
                if (!_matchedMtp.Remove(serial, out matched))
                {
                    return;
                }
            }

            try
            {
                foreach (var connection in matched)
                {
                    await operationLogFactory.CreateAsync($"USB device '{connection.Name}' disconnected");
                    if (connection.NotificationsEnabled && connection.NotifyOnDisconnect)
                    {
                        notifier.NotifyDeviceDisconnected(connection.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle MTP removal.");
            }
        }

        private async Task HandleRemovalAsync(string driveLetter)
        {
            List<MatchedConnection>? matched;
            lock (_gate)
            {
                _lastArrival.Remove(driveLetter);
                if (!_matched.Remove(driveLetter, out matched))
                {
                    return;
                }
            }

            try
            {
                foreach (var connection in matched)
                {
                    await operationLogFactory.CreateAsync($"USB device '{connection.Name}' disconnected");
                    if (connection.NotificationsEnabled && connection.NotifyOnDisconnect)
                    {
                        notifier.NotifyDeviceDisconnected(connection.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to handle USB removal for {Drive}.", driveLetter);
            }
        }

        private static string DescribeDevices(IReadOnlyList<MtpDevice> devices) =>
            devices.Count == 0 ? "(none)" : string.Join("; ", devices.Select(d => $"'{d.Name}' [{d.Serial}]"));

        private readonly record struct MatchedConnection(
            int ConnectionId, string Name, bool NotificationsEnabled, bool NotifyOnConnect, bool NotifyOnDisconnect);

        // ---- Win32 interop ----

        private const uint WM_DEVICECHANGE = 0x0219;
        private const uint WM_APP_QUIT = 0x8000 + 1; // WM_APP + 1
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const uint DBT_DEVNODES_CHANGED = 0x0007;
        private const uint DBT_DEVTYP_VOLUME = 0x00000002;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_HDR
        {
            public uint dbch_size;
            public uint dbch_devicetype;
            public uint dbch_reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_VOLUME
        {
            public uint dbcv_size;
            public uint dbcv_devicetype;
            public uint dbcv_reserved;
            public uint dbcv_unitmask;
            public ushort dbcv_flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
