using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Notifications
{
    /// <summary>
    /// Shows a small borderless, always-on-top progress window in the bottom-right of the screen while a profile
    /// with <see cref="Profile.ShowProgressWindow"/> is running. Multiple running profiles stack vertically.
    ///
    /// Driven by <see cref="IProfileStatusService"/> (status + percent). All window/GDI work runs on a dedicated
    /// message-loop thread (like <c>WindowsTrayService</c>); the status events arrive on background threads and
    /// just update a thread-safe state map then post a signal to the window thread to reconcile. Windows-only.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsProgressWindowService : IHostedService
    {
        private readonly IProfileStatusService _status;
        private readonly IDatabaseContextFactory _dbFactory;
        private readonly ILogger<WindowsProgressWindowService> _logger;

        // Profiles that currently want a window (id -> what to show). Mutated by the status events (any thread),
        // read by the window thread during a reconcile.
        private readonly ConcurrentDictionary<int, ProgressInfo> _state = new();

        // Window-thread-only maps.
        private readonly Dictionary<int, IntPtr> _windowByProfile = [];
        private readonly Dictionary<IntPtr, int> _profileByWindow = [];
        private readonly Dictionary<int, ProgressInfo> _shownInfo = [];

        private Thread? _thread;
        private IntPtr _msgHwnd;
        private WndProcDelegate? _wndProc; // kept alive so the native callback isn't collected

        // GDI resources (created on the window thread, freed on exit).
        private IntPtr _bgBrush, _borderBrush, _trackBrush, _fillBrush, _titleFont, _bodyFont;

        public WindowsProgressWindowService(
            IProfileStatusService status,
            IDatabaseContextFactory dbFactory,
            ILogger<WindowsProgressWindowService> logger)
        {
            _status = status;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Task.CompletedTask;
            }

            _status.Changed += OnStatusChanged;
            _status.ProgressChanged += OnProgressChanged;

            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "BackupService.Progress",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _status.Changed -= OnStatusChanged;
            _status.ProgressChanged -= OnProgressChanged;

            if (_msgHwnd != IntPtr.Zero)
            {
                PostMessage(_msgHwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            _thread?.Join(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        // ---- Status events (background threads) ----

        private void OnStatusChanged(int profileId)
        {
            try
            {
                if (_status.Get(profileId) == ProfileStatus.Running)
                {
                    var profile = LoadProfile(profileId);
                    if (profile is { ShowProgressWindow: true })
                    {
                        _state[profileId] = new ProgressInfo(profile.Name, ActionFor(profile.Type), Clamp(_status.GetProgress(profileId)));
                    }
                    else
                    {
                        _state.TryRemove(profileId, out _);
                    }
                }
                else
                {
                    // Any non-Running status ends the run — drop the window.
                    _state.TryRemove(profileId, out _);
                }

                Signal();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Progress window: failed to handle a status change for profile {ProfileId}.", profileId);
            }
        }

        private void OnProgressChanged(int profileId)
        {
            if (_state.TryGetValue(profileId, out var info))
            {
                _state[profileId] = info with { Percent = Clamp(_status.GetProgress(profileId)) };
                Signal();
            }
        }

        private void Signal()
        {
            if (_msgHwnd != IntPtr.Zero)
            {
                PostMessage(_msgHwnd, WM_APP_SYNC, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private Profile? LoadProfile(int profileId)
        {
            using var db = _dbFactory.CreateDbContext();
            return db.Profiles.AsNoTracking()
                .Where(p => p.Id == profileId)
                .Select(p => new Profile { Id = p.Id, Name = p.Name, Type = p.Type, ShowProgressWindow = p.ShowProgressWindow })
                .FirstOrDefault();
        }

        private static int Clamp(int? percent) => Math.Clamp(percent ?? 0, 0, 100);

        private static string ActionFor(ProfileType type) => type switch
        {
            ProfileType.ArchiveSync => "Creating archive",
            ProfileType.InstantSync => "Syncing",
            ProfileType.LightroomArchive => "Archiving",
            _ => "Backing up",
        };

        // ---- The window-loop thread ----

        private void MessageLoop()
        {
            try
            {
                _wndProc = WndProc;

                const string className = "BackupServiceProgressWindow";
                var wndClass = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className,
                    hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                };
                RegisterClass(ref wndClass);

                CreateGdiResources();

                // A message-only window to receive the cross-thread reconcile/quit signals.
                _msgHwnd = CreateWindowEx(0, className, "progress-signal", 0, 0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
                if (_msgHwnd == IntPtr.Zero)
                {
                    _logger.LogError("Failed to create the progress signal window (Win32 error {Error}).", Marshal.GetLastWin32Error());
                    return;
                }

                // Reconcile anything that accumulated before the window thread was ready.
                Reconcile();

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The progress window message loop ended unexpectedly.");
            }
            finally
            {
                DestroyAllWindows();
                FreeGdiResources();
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_APP_SYNC:
                    Reconcile();
                    return IntPtr.Zero;

                case WM_APP_QUIT:
                    DestroyAllWindows();
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                case WM_ERASEBKGND:
                    return (IntPtr)1; // WM_PAINT fully repaints, so skip the default erase (avoids flicker).

                case WM_PAINT:
                    Paint(hWnd);
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        // Brings the live windows in line with the desired state: create/update/destroy then restack.
        private void Reconcile()
        {
            var desired = _state.ToArray().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Remove windows whose profile is no longer running.
            foreach (var id in _windowByProfile.Keys.Where(id => !desired.ContainsKey(id)).ToList())
            {
                DestroyWindowFor(id);
            }

            // Create or update the rest.
            foreach (var (id, info) in desired)
            {
                if (_windowByProfile.TryGetValue(id, out var hwnd))
                {
                    if (!_shownInfo.TryGetValue(id, out var current) || current != info)
                    {
                        _shownInfo[id] = info;
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                }
                else
                {
                    CreateWindowFor(id, info);
                }
            }

            Restack();
        }

        private void CreateWindowFor(int id, ProgressInfo info)
        {
            var hwnd = CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                "BackupServiceProgressWindow", string.Empty, WS_POPUP,
                0, 0, Width, Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("Failed to create a progress window (Win32 error {Error}).", Marshal.GetLastWin32Error());
                return;
            }

            _windowByProfile[id] = hwnd;
            _profileByWindow[hwnd] = id;
            _shownInfo[id] = info;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        private void DestroyWindowFor(int id)
        {
            if (_windowByProfile.Remove(id, out var hwnd))
            {
                _profileByWindow.Remove(hwnd);
                _shownInfo.Remove(id);
                DestroyWindow(hwnd);
            }
        }

        private void DestroyAllWindows()
        {
            foreach (var hwnd in _windowByProfile.Values.ToList())
            {
                DestroyWindow(hwnd);
            }
            _windowByProfile.Clear();
            _profileByWindow.Clear();
            _shownInfo.Clear();
        }

        // Stacks the windows bottom-up from the bottom-right corner of the work area, ordered by profile id.
        private void Restack()
        {
            if (_windowByProfile.Count == 0)
            {
                return;
            }

            var work = new RECT();
            if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0))
            {
                return;
            }

            var ordered = _windowByProfile.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var x = work.Right - ScreenMargin - Width;
                var y = work.Bottom - ScreenMargin - (i + 1) * Height - i * Gap;
                SetWindowPos(ordered[i], HWND_TOPMOST, x, y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }

        // ---- Painting ----

        private void Paint(IntPtr hWnd)
        {
            var hdc = BeginPaint(hWnd, out var ps);
            try
            {
                if (!GetClientRect(hWnd, out var rc))
                {
                    return;
                }

                var info = _profileByWindow.TryGetValue(hWnd, out var id) && _shownInfo.TryGetValue(id, out var i)
                    ? i
                    : new ProgressInfo(string.Empty, string.Empty, 0);

                FillRect(hdc, ref rc, _bgBrush);
                FrameRect(hdc, ref rc, _borderBrush);

                SetBkMode(hdc, TRANSPARENT);

                // Title (profile name).
                var titleRect = new RECT { Left = Pad, Top = 12, Right = Width - Pad, Bottom = 34 };
                SelectObject(hdc, _titleFont);
                SetTextColor(hdc, ColTitle);
                DrawText(hdc, info.Title, -1, ref titleRect, DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

                // Sub-line: action + percent.
                var subRect = new RECT { Left = Pad, Top = 36, Right = Width - Pad, Bottom = 54 };
                SelectObject(hdc, _bodyFont);
                SetTextColor(hdc, ColMuted);
                DrawText(hdc, $"{info.Action} — {info.Percent}%", -1, ref subRect, DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

                // Progress bar: track then proportional fill.
                var track = new RECT { Left = Pad, Top = Height - 22, Right = Width - Pad, Bottom = Height - 14 };
                FillRect(hdc, ref track, _trackBrush);

                var fillWidth = (track.Right - track.Left) * info.Percent / 100;
                if (fillWidth > 0)
                {
                    var fill = track with { Right = track.Left + fillWidth };
                    FillRect(hdc, ref fill, _fillBrush);
                }
            }
            finally
            {
                EndPaint(hWnd, ref ps);
            }
        }

        private void CreateGdiResources()
        {
            _bgBrush = CreateSolidBrush(Rgb(0x24, 0x28, 0x30));
            _borderBrush = CreateSolidBrush(Rgb(0x3a, 0x40, 0x4a));
            _trackBrush = CreateSolidBrush(Rgb(0x33, 0x38, 0x42));
            _fillBrush = CreateSolidBrush(Rgb(0x4f, 0x8c, 0xff));
            _titleFont = CreateFontW(-15, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
            _bodyFont = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        }

        private void FreeGdiResources()
        {
            foreach (var obj in new[] { _bgBrush, _borderBrush, _trackBrush, _fillBrush, _titleFont, _bodyFont })
            {
                if (obj != IntPtr.Zero)
                {
                    DeleteObject(obj);
                }
            }
            _bgBrush = _borderBrush = _trackBrush = _fillBrush = _titleFont = _bodyFont = IntPtr.Zero;
        }

        private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);

        private static readonly int ColTitle = Rgb(0xf2, 0xf4, 0xf8);
        private static readonly int ColMuted = Rgb(0x9a, 0xa4, 0xb2);

        private sealed record ProgressInfo(string Title, string Action, int Percent);

        // ---- Win32 interop ----

        private const int Width = 340;
        private const int Height = 88;
        private const int ScreenMargin = 16;
        private const int Gap = 10;
        private const int Pad = 14;

        private const uint WM_APP = 0x8000;
        private const uint WM_APP_SYNC = WM_APP + 1;
        private const uint WM_APP_QUIT = WM_APP + 2;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;

        private const uint WS_POPUP = 0x80000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int SW_SHOWNOACTIVATE = 4;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const uint SPI_GETWORKAREA = 0x0030;
        private const int TRANSPARENT = 1;

        private const uint DT_SINGLELINE = 0x20;
        private const uint DT_NOPREFIX = 0x800;
        private const uint DT_END_ELLIPSIS = 0x8000;

        private const int FW_NORMAL = 400;
        private const int FW_SEMIBOLD = 600;
        private const int DEFAULT_CHARSET = 1;
        private const int CLEARTYPE_QUALITY = 5;
        private const int IDC_ARROW = 32512;

        private static readonly IntPtr HWND_MESSAGE = new(-3);
        private static readonly IntPtr HWND_TOPMOST = new(-1);

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
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public RECT rcPaint;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

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
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll")]
        private static extern int FrameRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
            int bItalic, int bUnderline, int bStrikeOut, int iCharSet, int iOutPrecision, int iClipPrecision,
            int iQuality, int iPitchAndFamily, string pszFaceName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
