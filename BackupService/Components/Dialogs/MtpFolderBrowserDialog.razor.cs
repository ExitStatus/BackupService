using BackupService.Connections.Usb;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Simple themed picker for a folder on an MTP device, over <see cref="IMtpDeviceInspector.ListDirectories"/>
    /// (device-absolute backslash paths). When <see cref="ConfineRoot"/> is set, navigation is confined to that
    /// subtree and the selected path is returned <b>relative</b> to it (the profile editors); otherwise the whole
    /// device is browsed and a device-absolute path is returned (the connection editor choosing the root).
    /// </summary>
    public partial class MtpFolderBrowserDialog : ComponentBase
    {
        [Inject]
        private IMtpDeviceInspector Inspector { get; set; } = default!;

        /// <summary>The device's MTP serial (its WPD DeviceId).</summary>
        [Parameter]
        public string Serial { get; set; } = string.Empty;

        /// <summary>Device-absolute folder to confine browsing to; null = whole device.</summary>
        [Parameter]
        public string? ConfineRoot { get; set; }

        /// <summary>Initial folder — relative to <see cref="ConfineRoot"/> when confined, else device-absolute.</summary>
        [Parameter]
        public string? InitialPath { get; set; }

        [Parameter]
        public EventCallback<string> OnSelect { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private string _currentPath = @"\"; // device-absolute
        private List<string> _dirs = [];
        private bool _busy;

        private string RootPath => string.IsNullOrEmpty(ConfineRoot) ? @"\" : NormalizeAbsolute(ConfineRoot);

        private bool CanGoUp => !string.Equals(_currentPath, RootPath, StringComparison.OrdinalIgnoreCase);

        protected override void OnInitialized()
        {
            _currentPath = CombineAbsolute(RootPath, InitialPath);
            Load();
        }

        private void Load()
        {
            _busy = true;
            try
            {
                _dirs = Inspector.ListDirectories(Serial, _currentPath).ToList();
            }
            finally
            {
                _busy = false;
            }
        }

        private void Navigate(string fullPath)
        {
            _currentPath = fullPath;
            Load();
        }

        private void Up()
        {
            if (!CanGoUp)
            {
                return;
            }

            var index = _currentPath.LastIndexOf('\\');
            var parent = index <= 0 ? @"\" : _currentPath[..index];
            if (!IsWithinRoot(parent))
            {
                parent = RootPath;
            }
            Navigate(parent);
        }

        private bool IsWithinRoot(string path) =>
            RootPath == @"\" || path.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase);

        private Task Select()
        {
            var result = string.IsNullOrEmpty(ConfineRoot)
                ? _currentPath                          // device-absolute
                : RelativeTo(RootPath, _currentPath);   // relative to the confine root
            return OnSelect.InvokeAsync(result);
        }

        private static string DisplayName(string fullPath)
        {
            var index = fullPath.LastIndexOf('\\');
            return index < 0 ? fullPath : fullPath[(index + 1)..];
        }

        private static string NormalizeAbsolute(string path)
        {
            var normalized = path.Replace('/', '\\').TrimEnd('\\');
            return string.IsNullOrEmpty(normalized) ? @"\" : (normalized.StartsWith('\\') ? normalized : @"\" + normalized);
        }

        // Combine a device-absolute root with a path that's relative to it (or device-absolute when no confine).
        private string CombineAbsolute(string root, string? path)
        {
            var rel = (path ?? string.Empty).Replace('/', '\\').Trim('\\');
            if (string.IsNullOrEmpty(ConfineRoot))
            {
                // No confine — InitialPath is itself device-absolute (or empty → root).
                return string.IsNullOrEmpty(rel) ? @"\" : NormalizeAbsolute(path!);
            }
            return rel.Length == 0 ? root : $@"{root.TrimEnd('\\')}\{rel}";
        }

        private static string RelativeTo(string root, string fullPath)
        {
            if (root == @"\")
            {
                return fullPath.Trim('\\');
            }
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath[root.Length..].Trim('\\');
            }
            return fullPath.Trim('\\');
        }
    }
}
