using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Options;

namespace BackupService.Notifications
{
    /// <summary>
    /// Windows system-tray integration: owns a notification-area icon and shows balloon tips, and serves as
    /// the <see cref="IDesktopNotifier"/>. All Shell_NotifyIcon calls run on a dedicated message-loop
    /// thread (a hidden message-only window); other threads communicate by posting window messages.
    ///
    /// The icon is present whenever "Show tray icon" <b>or</b> "Allow notifications" is on (a balloon attaches
    /// to a tray icon). Double-clicking opens the admin UI; a right-click menu offers Open / Exit. Registered
    /// only on Windows (three roles: singleton, <see cref="IDesktopNotifier"/>, hosted service).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsTrayService : IHostedService, IDesktopNotifier
    {
        private readonly IAppOptionsService _options;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<WindowsTrayService> _logger;
        private readonly string _uiUrl;

        private readonly ConcurrentQueue<BalloonRequest> _pending = new();
        private Thread? _thread;
        private IntPtr _hwnd;
        private IntPtr _icon;
        private bool _iconAdded;
        private uint _taskbarCreated; // the "TaskbarCreated" broadcast — re-add the icon when Explorer restarts
        private WndProcDelegate? _wndProc; // kept alive so the native callback isn't collected

        private volatile bool _showTrayIcon;
        private volatile bool _allowNotifications;

        public WindowsTrayService(
            IAppOptionsService options,
            IHostApplicationLifetime lifetime,
            IConfiguration configuration,
            ILogger<WindowsTrayService> logger)
        {
            _options = options;
            _lifetime = lifetime;
            _logger = logger;
            _uiUrl = ResolveUiUrl(configuration);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Task.CompletedTask;
            }

            try
            {
                var settings = _options.GetSettingsAsync(cancellationToken).GetAwaiter().GetResult();
                _showTrayIcon = settings.ShowTrayIcon;
                _allowNotifications = settings.AllowNotifications;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read app options for the tray icon.");
            }

            _options.Changed += OnOptionsChanged;

            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "BackupService.Tray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _options.Changed -= OnOptionsChanged;

            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            _thread?.Join(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        public void NotifyBackupStarted(string profileName, ProfileType type) =>
            Enqueue("Backup started", $"'{profileName}' ({type.GetDescription()}) started.", NIIF_INFO);

        public void NotifyBackupCompleted(string profileName, ProfileType type, RunOutcome outcome) =>
            Enqueue("Backup completed", $"'{profileName}' ({type.GetDescription()}) — {outcome.GetDescription()}", InfoFlagFor(outcome));

        public void NotifyTaskCompleted(string taskName, RunOutcome outcome) =>
            Enqueue("Scheduled task completed", $"'{taskName}' — {outcome.GetDescription()}", InfoFlagFor(outcome));

        public void NotifyDeviceConnected(string deviceName) =>
            Enqueue($"{deviceName} Connected", "The USB device is connected.", NIIF_INFO);

        public void NotifyDeviceDisconnected(string deviceName) =>
            Enqueue($"{deviceName} Disconnected", "The USB device was disconnected.", NIIF_INFO);

        private void Enqueue(string title, string message, int infoFlag)
        {
            if (!_allowNotifications || _hwnd == IntPtr.Zero)
            {
                return;
            }

            _pending.Enqueue(new BalloonRequest(title, message, infoFlag));
            PostMessage(_hwnd, WM_APP_BALLOON, IntPtr.Zero, IntPtr.Zero);
        }

        private void OnOptionsChanged(Database.AppOptions settings)
        {
            // Runs on the caller's thread (the Blazor circuit). Just cache the values and signal the loop —
            // never block here (a sync DB read would deadlock the circuit).
            _showTrayIcon = settings.ShowTrayIcon;
            _allowNotifications = settings.AllowNotifications;

            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
            }
        }

        // ---- The message-loop thread ----

        private void MessageLoop()
        {
            try
            {
                _wndProc = WndProc;
                _icon = LoadAppIcon();

                const string className = "BackupServiceTrayWindow";
                var wndClass = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className,
                };
                RegisterClass(ref wndClass);

                // "TaskbarCreated" is broadcast when Explorer (re)starts; we re-add the icon on it. Broadcasts
                // don't reach message-only windows, so use a normal top-level window that is simply never shown.
                _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

                _hwnd = CreateWindowEx(0, className, "Backup Service", WS_OVERLAPPED, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    _logger.LogError("Failed to create the tray window (Win32 error {Error}).", Marshal.GetLastWin32Error());
                    return;
                }

                ApplyIconVisibility();

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The tray icon message loop ended unexpectedly.");
            }
            finally
            {
                RemoveIcon();
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Explorer restarted (or this is the first creation after a crash): re-add the icon if it should show.
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                _iconAdded = false;
                ApplyIconVisibility();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_TRAYCALLBACK:
                    var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                    if (mouse == WM_LBUTTONDBLCLK)
                    {
                        OpenUi();
                    }
                    else if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
                    {
                        ShowContextMenu();
                    }
                    return IntPtr.Zero;

                case WM_COMMAND:
                    var command = (int)(wParam.ToInt64() & 0xFFFF);
                    if (command == IdOpen)
                    {
                        OpenUi();
                    }
                    else if (command == IdExit)
                    {
                        _lifetime.StopApplication();
                    }
                    return IntPtr.Zero;

                case WM_APP_BALLOON:
                    DrainBalloons();
                    return IntPtr.Zero;

                case WM_APP_REFRESH:
                    ApplyIconVisibility();
                    return IntPtr.Zero;

                case WM_APP_QUIT:
                    RemoveIcon();
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void ApplyIconVisibility()
        {
            var shouldShow = _showTrayIcon || _allowNotifications;
            if (shouldShow && !_iconAdded)
            {
                AddIcon();
            }
            else if (!shouldShow && _iconAdded)
            {
                RemoveIcon();
            }
        }

        private void AddIcon()
        {
            var data = NewIconData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = (int)WM_TRAYCALLBACK;
            data.hIcon = _icon;
            data.szTip = "Backup Service";
            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!_iconAdded)
            {
                _logger.LogError("Shell_NotifyIcon(NIM_ADD) failed (Win32 error {Error}).", Marshal.GetLastWin32Error());
            }
        }

        private void RemoveIcon()
        {
            if (!_iconAdded)
            {
                return;
            }

            var data = NewIconData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconAdded = false;
        }

        private void DrainBalloons()
        {
            // A balloon needs the icon present; ensure it before showing.
            if (!_iconAdded)
            {
                AddIcon();
            }

            while (_pending.TryDequeue(out var balloon))
            {
                var data = NewIconData();
                data.uFlags = NIF_INFO;
                data.szInfoTitle = balloon.Title;
                data.szInfo = balloon.Message;
                data.dwInfoFlags = balloon.InfoFlag;
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
        }

        private NOTIFYICONDATA NewIconData() => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            // The fixed-buffer string fields must never be null — marshaling a null ByValTStr throws.
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                AppendMenu(menu, MF_STRING, IdOpen, "Open Backup Service");
                AppendMenu(menu, MF_STRING, IdExit, "Exit");

                GetCursorPos(out var point);
                // Required so the menu dismisses when the user clicks elsewhere.
                SetForegroundWindow(_hwnd);
                TrackPopupMenuEx(menu, TPM_RIGHTBUTTON, point.X, point.Y, _hwnd, IntPtr.Zero);
                PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        private void OpenUi()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_uiUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open the admin UI at {Url}.", _uiUrl);
            }
        }

        private IntPtr LoadAppIcon()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && ExtractIconEx(exePath, 0, out _, out var small, 1) > 0 && small != IntPtr.Zero)
                {
                    return small;
                }
            }
            catch
            {
                // Fall through to the stock icon.
            }

            return LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        private static string ResolveUiUrl(IConfiguration configuration)
        {
            var url = configuration["Kestrel:Endpoints:Http:Url"];
            return string.IsNullOrWhiteSpace(url) ? "http://localhost:55000" : url;
        }

        private static int InfoFlagFor(RunOutcome outcome) => outcome switch
        {
            RunOutcome.Success => NIIF_INFO,
            RunOutcome.CompletedWithWarnings => NIIF_WARNING,
            _ => NIIF_ERROR, // CompletedWithErrors / Failed
        };

        private readonly record struct BalloonRequest(string Title, string Message, int InfoFlag);

        // ---- Win32 interop ----

        private const int TrayIconId = 1;
        private const int IdOpen = 1;
        private const int IdExit = 2;

        private const int WM_NULL = 0x0000;
        private const int WM_COMMAND = 0x0111;
        private const uint WM_APP = 0x8000;
        private const uint WM_TRAYCALLBACK = 0x0400 + 1; // WM_USER + 1
        private const uint WM_APP_BALLOON = WM_APP + 1;
        private const uint WM_APP_REFRESH = WM_APP + 2;
        private const uint WM_APP_QUIT = WM_APP + 3;

        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_CONTEXTMENU = 0x007B;

        private const int NIM_ADD = 0;
        private const int NIM_MODIFY = 1;
        private const int NIM_DELETE = 2;

        private const int NIF_MESSAGE = 0x01;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;
        private const int NIF_INFO = 0x10;

        private const int NIIF_INFO = 0x01;
        private const int NIIF_WARNING = 0x02;
        private const int NIIF_ERROR = 0x03;

        private const uint MF_STRING = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const int IDI_APPLICATION = 32512;
        private const uint WS_OVERLAPPED = 0x00000000;

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

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

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
