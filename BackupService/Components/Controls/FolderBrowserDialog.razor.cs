using System.Globalization;
using BackupService.FileSystem;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Modal that browses folders on the server in a Windows Explorer style (left navigation
    /// tree + address-bar breadcrumb + details list) and returns the chosen folder path via
    /// <see cref="OnSelect"/>.
    /// </summary>
    public partial class FolderBrowserDialog : ComponentBase
    {
        [Inject]
        private IFolderBrowser Browser { get; set; } = default!;

        [Parameter]
        public string? InitialPath { get; set; }

        [Parameter]
        public EventCallback<string> OnSelect { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private IReadOnlyList<DriveEntry> _drives = [];
        private IReadOnlyList<FolderEntry> _quickAccess = [];
        private IReadOnlyList<FolderEntry> _entries = [];

        // null = the "This PC" root (the details list then shows drives).
        private string? _currentPath;
        private string? _selectedPath;
        private string _folderText = string.Empty;

        private readonly List<string?> _history = [];
        private int _historyIndex = -1;

        private List<Crumb> _breadcrumb = [];

        private bool _atThisPc => _currentPath is null;

        protected override void OnInitialized()
        {
            _drives = Browser.GetDrives();
            _quickAccess = Browser.GetQuickAccess();

            if (!string.IsNullOrWhiteSpace(InitialPath) && Directory.Exists(InitialPath))
            {
                Navigate(InitialPath);
            }
            else
            {
                Navigate(null);
            }
        }

        private bool CanBack => _historyIndex > 0;

        private bool CanForward => _historyIndex < _history.Count - 1;

        private bool CanUp => _currentPath is not null;

        private void Navigate(string? path)
        {
            // Drop any forward history when navigating somewhere new.
            if (_historyIndex < _history.Count - 1)
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            }

            _history.Add(path);
            _historyIndex = _history.Count - 1;
            Load(path);
        }

        private void Back()
        {
            if (CanBack)
            {
                _historyIndex--;
                Load(_history[_historyIndex]);
            }
        }

        private void Forward()
        {
            if (CanForward)
            {
                _historyIndex++;
                Load(_history[_historyIndex]);
            }
        }

        private void Up()
        {
            if (_currentPath is null)
            {
                return;
            }

            Navigate(Browser.GetParent(_currentPath)); // null parent → back to This PC
        }

        private void Load(string? path)
        {
            _currentPath = path;
            _entries = path is null
                ? _drives.Select(d => new FolderEntry(d.RootPath, d.Label, null)).ToList()
                : Browser.GetDirectories(path);

            // Default the candidate selection to the folder we just opened.
            _selectedPath = path;
            _folderText = path ?? string.Empty;
            _breadcrumb = BuildBreadcrumb(path);
        }

        private void SelectEntry(FolderEntry entry)
        {
            _selectedPath = entry.FullPath;
            _folderText = entry.FullPath;
        }

        private bool IsSelected(FolderEntry entry) =>
            string.Equals(entry.FullPath, _selectedPath, StringComparison.OrdinalIgnoreCase);

        private async Task SelectAsync()
        {
            if (!string.IsNullOrWhiteSpace(_folderText))
            {
                await OnSelect.InvokeAsync(_folderText);
            }
        }

        private List<Crumb> BuildBreadcrumb(string? path)
        {
            var crumbs = new List<Crumb> { new("This PC", null) };
            if (path is null)
            {
                return crumbs;
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                crumbs.Add(new(path, path));
                return crumbs;
            }

            var driveLabel = _drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase))?.Label
                ?? root.TrimEnd(Path.DirectorySeparatorChar);
            crumbs.Add(new(driveLabel, root));

            var rest = path.Substring(root.Length).Trim(Path.DirectorySeparatorChar);
            if (rest.Length == 0)
            {
                return crumbs;
            }

            var accumulated = root;
            foreach (var part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = Path.Combine(accumulated, part);
                crumbs.Add(new(part, accumulated));
            }

            return crumbs;
        }

        private static string FormatDate(DateTimeOffset? value) =>
            value?.ToString("dd/MM/yyyy h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() ?? string.Empty;

        private sealed record Crumb(string Label, string? Path);
    }
}
