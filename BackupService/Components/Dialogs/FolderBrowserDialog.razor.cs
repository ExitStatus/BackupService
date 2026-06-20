using System.Globalization;
using BackupService.FileSystem;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
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

        /// <summary>
        /// When set, browsing is confined to this folder and its subtree — the sidebar shows only this
        /// root, Up stops here, the breadcrumb starts here, and a path outside it can't be navigated to or
        /// selected. Null = browse the whole machine (drives + Quick access).
        /// </summary>
        [Parameter]
        public string? RootPath { get; set; }

        /// <summary>
        /// When true, a "New folder" button is offered (creating a sub-folder of the current folder).
        /// Off by default — only the target-folder picker enables it (you don't create source/exclude folders).
        /// </summary>
        [Parameter]
        public bool AllowCreateFolder { get; set; }

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

        // The normalised browse root (no trailing separator), or null when unconstrained.
        private string? _root;

        private bool _atThisPc => _currentPath is null;

        private bool Rooted => _root is not null;

        private string RootLabel => _root is null
            ? string.Empty
            : Path.GetFileName(_root) is { Length: > 0 } name ? name : _root;

        protected override void OnInitialized()
        {
            _drives = Browser.GetDrives();
            _quickAccess = Browser.GetQuickAccess();

            if (!string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath))
            {
                _root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var start = !string.IsNullOrWhiteSpace(InitialPath) && IsWithinRoot(InitialPath!) ? InitialPath! : _root;
                Navigate(start);
            }
            else if (!string.IsNullOrWhiteSpace(InitialPath) && Directory.Exists(InitialPath))
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

        private bool CanUp => Rooted
            ? _currentPath is not null && !PathEquals(_currentPath, _root)
            : _currentPath is not null;

        /// <summary>True when <paramref name="path"/> is the root or sits inside it (always true when unrooted).</summary>
        private bool IsWithinRoot(string path)
        {
            if (_root is null)
            {
                return true;
            }

            var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.Equals(_root, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathEquals(string? a, string? b) => string.Equals(
            a?.TrimEnd(Path.DirectorySeparatorChar),
            b?.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        private void Navigate(string? path)
        {
            // When rooted, never leave the subtree (This PC or any path above/outside the root snaps back).
            if (Rooted && (path is null || !IsWithinRoot(path)))
            {
                path = _root;
            }

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

            if (Rooted && PathEquals(_currentPath, _root))
            {
                return; // already at the root — can't go higher
            }

            Navigate(Browser.GetParent(_currentPath)); // null parent â†’ back to This PC (clamped to root when rooted)
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
            _creatingFolder = false; // drop any in-progress "new folder" entry on navigation
            _newFolderError = null;
        }

        private void SelectEntry(FolderEntry entry)
        {
            _selectedPath = entry.FullPath;
            _folderText = entry.FullPath;
        }

        private bool IsSelected(FolderEntry entry) =>
            string.Equals(entry.FullPath, _selectedPath, StringComparison.OrdinalIgnoreCase);

        private bool CanSelect =>
            !string.IsNullOrWhiteSpace(_folderText) && (!Rooted || IsWithinRoot(_folderText));

        // ---- New folder ----

        private bool _creatingFolder;
        private string _newFolderName = string.Empty;
        private string? _newFolderError;
        private ElementReference _newFolderInput;
        private bool _focusNewFolder; // set when the create bar opens; honoured in OnAfterRenderAsync

        // A new folder is created under the currently open folder, so there must be a real path open.
        private bool CanCreateFolder => AllowCreateFolder && _currentPath is not null;

        private void BeginCreateFolder()
        {
            _newFolderName = string.Empty;
            _newFolderError = null;
            _creatingFolder = true;
            _focusNewFolder = true; // focus the name box once it renders
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (_focusNewFolder)
            {
                _focusNewFolder = false;
                await _newFolderInput.FocusAsync();
            }
        }

        private void CancelCreateFolder()
        {
            _creatingFolder = false;
            _newFolderError = null;
        }

        private void OnNewFolderKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
        {
            if (e.Key is "Enter" or "NumpadEnter")
            {
                ConfirmCreateFolder();
            }
            else if (e.Key == "Escape")
            {
                CancelCreateFolder();
            }
        }

        private void ConfirmCreateFolder()
        {
            if (_currentPath is null)
            {
                return;
            }

            var name = _newFolderName.Trim();
            if (name.Length == 0)
            {
                _newFolderError = "Enter a folder name.";
                return;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _newFolderError = "The name contains invalid characters.";
                return;
            }
            if (_entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _newFolderError = $"A folder named '{name}' already exists here.";
                return;
            }

            string created;
            try
            {
                created = Browser.CreateDirectory(_currentPath, name);
            }
            catch (Exception ex)
            {
                _newFolderError = $"Couldn't create the folder: {ex.Message}";
                return;
            }

            _creatingFolder = false;
            // Re-list the current folder so the new one appears, then select it.
            Load(_currentPath);
            _selectedPath = created;
            _folderText = created;
        }

        private async Task SelectAsync()
        {
            if (CanSelect)
            {
                await OnSelect.InvokeAsync(_folderText);
            }
        }

        private List<Crumb> BuildBreadcrumb(string? path)
        {
            // When rooted, the breadcrumb starts at the root (not This PC) and never shows above it.
            if (Rooted)
            {
                var rooted = new List<Crumb> { new(RootLabel, _root) };
                if (path is null || PathEquals(path, _root))
                {
                    return rooted;
                }

                var relative = path[_root!.Length..].Trim(Path.DirectorySeparatorChar);
                var rootedAccumulated = _root!;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    rootedAccumulated = Path.Combine(rootedAccumulated, part);
                    rooted.Add(new(part, rootedAccumulated));
                }
                return rooted;
            }

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
