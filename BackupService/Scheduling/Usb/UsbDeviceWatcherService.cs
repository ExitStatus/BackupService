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
    /// notifies, and runs any enabled OneWaySync/ArchiveSync profile that uses the connection as its source <b>or</b>
    /// target — provided every USB connection the profile references is currently connected (so a both-USB profile
    /// waits for both devices). On disconnect it logs + notifies. Windows-only; registered as a singleton + hosted
    /// service.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class UsbDeviceWatcherService(
        IDatabaseContextFactory contextFactory,
        IUsbDeviceInspector inspector,
        IMtpDeviceInspector mtpInspector,
        IUsbConnector usbConnector,
        UsbTriggeredRunner triggeredRunner,
        IOperationLogFactory operationLogFactory,
        IDesktopNotifier notifier,
        ILogger<UsbDeviceWatcherService> logger) : IHostedService
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stopping = new();
        // Drive letter -> the connections it matched on arrival, so a later removal can be logged (the device is
        // gone by then and can't be re-read).
        private readonly Dictionary<string, List<MatchedConnection>> _matched = new(StringComparer.OrdinalIgnoreCase);
        // Debounce duplicate arrival broadcasts for the same drive.
        private readonly Dictionary<string, DateTime> _lastArrival = new(StringComparer.OrdinalIgnoreCase);

        // MTP devices raise no volume event; instead we re-scan portable devices on a device-tree change and compare
        // with what was there before to spot arrivals/removals. Seeded at start so already-attached devices aren't
        // "new"; scans wait for the seed.
        private readonly MtpPresenceTracker _mtpPresence = new();
        private readonly Dictionary<string, List<MatchedConnection>> _matchedMtp = new(StringComparer.OrdinalIgnoreCase);
        private Task _mtpSeeded = Task.CompletedTask;
        private RescanCoalescer? _mtpRescan; // folds a burst of device-tree changes into one scan sequence

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
            _mtpRescan = new RescanCoalescer(ScanMtpSequenceAsync);
            _mtpSeeded = Task.Run(SeedMtpAsync);

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
            _stopping.Cancel();
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

        // Shared "matched connection(s) arrived" path: log + notify, then run the enabled OneWaySync/ArchiveSync
        // profiles whose source or target is one of them — but only when every USB device the profile references is
        // currently connected (so a both-USB profile fires when the second device arrives). Used by both the
        // mass-storage (volume) and MTP arrival paths.
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

            // Candidates: enabled OneWaySync/ArchiveSync/TwoWaySync whose source OR target is one of the arrived connections.
            var candidates = await db.Profiles
                .AsNoTracking()
                .Where(p => p.Enabled
                    && (p.Type == ProfileType.OneWaySync || p.Type == ProfileType.ArchiveSync || p.Type == ProfileType.TwoWaySync)
                    && ((p.SourceConnectionId != null && connectionIds.Contains(p.SourceConnectionId.Value))
                        || (p.TargetConnectionId != null && connectionIds.Contains(p.TargetConnectionId.Value))))
                .Select(p => new { p.Id, p.SourceConnectionId, p.TargetConnectionId, p.EjectAfterRun, p.NotificationsEnabled, p.NotifyOnEject })
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return;
            }

            // Load every USB connection a candidate references (source or target) so each can be checked for "plugged in".
            var referencedIds = candidates
                .SelectMany(c => new[] { c.SourceConnectionId, c.TargetConnectionId })
                .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

            var usbConnections = await db.Connections
                .AsNoTracking()
                .Where(c => c.Type == ConnectionType.Usb && referencedIds.Contains(c.Id))
                .Include(c => c.Usb)
                .ToListAsync();

            var usbById = usbConnections.Where(c => c.Usb is not null).ToDictionary(c => c.Id, c => c.Usb!);
            var connectivity = new Dictionary<int, bool>();
            bool IsConnected(int id)
            {
                if (connectivity.TryGetValue(id, out var known))
                {
                    return known;
                }
                var result = usbById.TryGetValue(id, out var usb) && IsUsbDeviceConnected(usb);
                connectivity[id] = result;
                return result;
            }

            // Split into the profiles that can run now (all their USB devices connected) and those still waiting.
            var runnable = new List<int>();
            var ejectPlan = new Dictionary<int, bool>(); // mass-storage USB connection id -> notify on eject
            foreach (var candidate in candidates)
            {
                if (!UsbTriggerEvaluator.AllRequiredUsbConnected(
                        candidate.SourceConnectionId, candidate.TargetConnectionId, usbById.ContainsKey, IsConnected))
                {
                    logger.LogInformation("USB connected — profile {ProfileId} is waiting for its other USB device.", candidate.Id);
                    continue;
                }

                logger.LogInformation("USB device connected — running profile {ProfileId}.", candidate.Id);
                runnable.Add(candidate.Id);

                if (candidate.EjectAfterRun)
                {
                    var notify = candidate.NotificationsEnabled && candidate.NotifyOnEject;
                    foreach (var connectionId in new[] { candidate.SourceConnectionId, candidate.TargetConnectionId })
                    {
                        if (connectionId is { } id && usbById.TryGetValue(id, out var usb) && usb.Kind == UsbDeviceKind.MassStorage)
                        {
                            ejectPlan[id] = ejectPlan.GetValueOrDefault(id) || notify;
                        }
                    }
                }
            }

            if (runnable.Count == 0)
            {
                return;
            }

            var ejects = ejectPlan
                .Select(e => (Usb: usbById[e.Key], ConnectionId: e.Key, Notify: e.Value))
                .Select(e => new UsbEjectRequest(e.ConnectionId,
                    new UsbConnectionInfo(e.Usb.Kind, e.Usb.HardwareSerial, e.Usb.VolumeSerial, e.Usb.MtpSerial, e.Usb.RootFolder),
                    e.Usb.DeviceLabel, e.Notify))
                .ToList();

            // Runs the batch (they serialise per device via IUsbRunGate), then ejects once every run using the device
            // has finished — see UsbTriggeredRunner.
            _ = Task.Run(() => triggeredRunner.RunThenEjectAsync(runnable, ejects, _stopping.Token));
        }

        // Whether the device a USB connection is bound to is currently connected (mass-storage: a current mount path;
        // MTP: enumerable as a portable device).
        private bool IsUsbDeviceConnected(UsbConnectionSettings usb)
        {
            if (usb.Kind == UsbDeviceKind.Mtp)
            {
                return !string.IsNullOrEmpty(usb.MtpSerial) && mtpInspector.IsConnected(usb.MtpSerial);
            }

            var info = new UsbConnectionInfo(usb.Kind, usb.HardwareSerial, usb.VolumeSerial, usb.MtpSerial, usb.RootFolder);
            return usbConnector.FindMountPath(info) is not null;
        }

        // ---- MTP (portable-device) detection ----

        // A device-tree change usually comes as a burst; the coalescer folds it into one scan sequence, plus one more
        // if a change arrives while a sequence is already running.
        private void OnDeviceNodesChanged() => _mtpRescan?.Request();

        private async Task SeedMtpAsync()
        {
            // A device that can't be read now would otherwise look like an arrival on the first scan and run its
            // profiles, so give an incomplete list a couple more tries.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var complete = mtpInspector.TryEnumerateMtpDevices(out var devices);
                    if (complete || attempt == 3)
                    {
                        lock (_gate)
                        {
                            _mtpPresence.Seed(devices);
                        }
                        logger.LogInformation("USB watcher: seeded {Count} already-connected portable (MTP) device(s): {Devices}",
                            devices.Count, DescribeDevices(devices));
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), _stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to seed the MTP device snapshot.");
                    return;
                }
            }
        }

        // A portable device (especially a camera that prompts on-screen for a USB mode) can take several seconds
        // to register with WPD after the device-tree change fires. Scan a few times over ~8s so a late arrival is
        // still caught — MtpPresenceTracker keeps the repeats idempotent (each device fires once). A device that has
        // just gone missing is only confirmed removed by a later scan, so keep scanning briefly until that's settled.
        private async Task ScanMtpSequenceAsync()
        {
            await _mtpSeeded;
            logger.LogDebug("USB watcher: device-tree changed — scanning for portable (MTP) devices.");
            int[] delaysMs = [0, 1500, 3000, 5000, 8000];
            foreach (var delay in delaysMs)
            {
                if (delay > 0)
                {
                    await Task.Delay(delay, _stopping.Token);
                }
                await ScanMtpAsync();
            }

            for (var extra = 0; extra < 3 && HasUnconfirmedMtpRemovals(); extra++)
            {
                await Task.Delay(1500, _stopping.Token);
                await ScanMtpAsync();
            }
        }

        private bool HasUnconfirmedMtpRemovals()
        {
            lock (_gate)
            {
                return _mtpPresence.HasUnconfirmedRemovals;
            }
        }

        private async Task ScanMtpAsync()
        {
            try
            {
                if (!mtpInspector.TryEnumerateMtpDevices(out var present))
                {
                    // An incomplete list would make the missing devices look unplugged — wait for the next scan.
                    logger.LogDebug("USB watcher: the portable-device list couldn't be read in full; ignoring this scan.");
                    return;
                }
                logger.LogDebug("USB watcher: MTP scan found {Count} portable device(s): {Devices}",
                    present.Count, DescribeDevices(present));

                IReadOnlyList<MtpDevice> arrived;
                IReadOnlyList<string> removed;
                lock (_gate)
                {
                    (arrived, removed) = _mtpPresence.Update(present);
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
