using BackupService.Connections.GoogleDrive;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Themed picker for a folder on Google Drive. Browses relative to My Drive root via
    /// <see cref="IGoogleDriveConnector.ListDirectoriesAsync"/> and returns the chosen name-path through
    /// <see cref="OnSelect"/>. The Google counterpart of <see cref="SmbFolderBrowserDialog"/>.
    /// </summary>
    public partial class GoogleDriveFolderBrowserDialog : ComponentBase
    {
        [Inject]
        private IGoogleDriveConnector Connector { get; set; } = default!;

        [Parameter]
        public GoogleDriveConnectionInfo Info { get; set; } = default!;

        [Parameter]
        public string? InitialRelativePath { get; set; }

        /// <summary>
        /// When true, browsing is confined to the connection's <see cref="GoogleDriveConnectionInfo.RootFolder"/>
        /// and its subtree, and the chosen path is returned <b>relative to that root</b> (used by the profile
        /// editors). When false (the connection editor choosing the root itself) the whole Drive is browsed and
        /// a path relative to My Drive root is returned.
        /// </summary>
        [Parameter]
        public bool ConfineToRoot { get; set; }

        /// <summary>When true, a "New folder" button creates a sub-folder of the current folder.</summary>
        [Parameter]
        public bool AllowCreateFolder { get; set; }

        [Parameter]
        public EventCallback<string> OnSelect { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        // Current folder, relative to the browse root ("" = the root). Backslash-separated.
        private string _currentPath = string.Empty;
        private string? _selected;
        private List<string> _dirs = [];
        private bool _busy;
        private string? _error;

        private bool _creating;
        private string _newName = string.Empty;
        private string? _createError;
        private bool _focusNewFolder;
        private ElementReference _newFolderInput;

        private IEnumerable<string> Segments =>
            _currentPath.Length == 0 ? [] : _currentPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // The name-path the browse is confined to (the connection's RootFolder when confined, else My Drive root).
        private string Root => ConfineToRoot ? Normalize(Info.RootFolder) : string.Empty;

        private string RootLabel
        {
            get
            {
                var root = Root;
                return ConfineToRoot && root.Length > 0 ? root : "My Drive";
            }
        }

        // Maps a root-relative path to the full path (relative to My Drive root) the connector expects.
        private string Absolute(string relative)
        {
            var root = Root;
            if (root.Length == 0)
            {
                return relative;
            }
            return relative.Length == 0 ? root : $@"{root}\{relative}";
        }

        protected override async Task OnInitializedAsync()
        {
            _currentPath = Normalize(InitialRelativePath);
            await LoadAsync();
        }

        private async Task NavigateAsync(string path)
        {
            _currentPath = Normalize(path);
            _selected = null;
            await LoadAsync();
        }

        private Task UpAsync()
        {
            if (_currentPath.Length == 0)
            {
                return Task.CompletedTask;
            }

            var index = _currentPath.LastIndexOf('\\');
            var parent = index < 0 ? string.Empty : _currentPath[..index];
            return NavigateAsync(parent);
        }

        private async Task LoadAsync()
        {
            _busy = true;
            _error = null;
            try
            {
                _dirs = (await Connector.ListDirectoriesAsync(Info, Absolute(_currentPath))).ToList();
            }
            catch (Exception ex)
            {
                _dirs = [];
                _error = $"Could not browse: {ex.Message}";
            }
            finally
            {
                _busy = false;
            }
        }

        private Task Select() => OnSelect.InvokeAsync(_selected ?? _currentPath);

        private void StartCreate()
        {
            _creating = true;
            _newName = string.Empty;
            _createError = null;
            _focusNewFolder = true;
        }

        private void CancelCreate()
        {
            _creating = false;
            _createError = null;
        }

        private async Task OnNewFolderKeyDown(KeyboardEventArgs e)
        {
            if (e.Key is "Enter")
            {
                await CreateAsync();
            }
            else if (e.Key is "Escape")
            {
                CancelCreate();
            }
        }

        private async Task CreateAsync()
        {
            var name = _newName.Trim();
            _createError = ValidateName(name);
            if (_createError is not null)
            {
                return;
            }

            var relative = _currentPath.Length == 0 ? name : $@"{_currentPath}\{name}";

            _busy = true;
            try
            {
                await Connector.CreateDirectoryAsync(Info, Absolute(relative));
            }
            catch (Exception ex)
            {
                _createError = ex.Message;
                return;
            }
            finally
            {
                _busy = false;
            }

            _creating = false;
            await LoadAsync();
            _selected = relative;
        }

        private string? ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Enter a folder name.";
            }
            if (name.IndexOfAny(['\\', '/']) >= 0)
            {
                return "The name contains invalid characters.";
            }
            if (_dirs.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)))
            {
                return "A folder with that name already exists here.";
            }
            return null;
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (_focusNewFolder)
            {
                _focusNewFolder = false;
                try
                {
                    await _newFolderInput.FocusAsync();
                }
                catch
                {
                    // best-effort focus
                }
            }
        }

        private static string Normalize(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').Trim('\\');
    }
}
