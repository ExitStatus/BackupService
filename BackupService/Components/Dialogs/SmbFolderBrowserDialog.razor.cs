using BackupService.Connections;
using BackupService.Connections.Smb;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Simple themed picker for a folder on a remote SMB share. Browses relative to the share root via
    /// <see cref="ISmbConnector.ListDirectoriesAsync"/> and returns the chosen path (relative to the
    /// share root) through <see cref="OnSelect"/>.
    /// </summary>
    public partial class SmbFolderBrowserDialog : ComponentBase
    {
        [Inject]
        private ISmbConnector SmbConnector { get; set; } = default!;

        [Parameter]
        public SmbConnectionInfo Info { get; set; } = default!;

        [Parameter]
        public string? InitialRelativePath { get; set; }

        /// <summary>
        /// When true, browsing is confined to the connection's <see cref="SmbConnectionInfo.RootFolder"/> and
        /// its subtree (you can't navigate above it), and the chosen path is returned <b>relative to that
        /// root</b>. Used by the profile editors (the stored path is root-relative). When false (the
        /// connection editor choosing the root itself) the whole share is browsed and a share-relative path
        /// is returned.
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

        // Current folder, relative to the browse root ("" = the root — share root, or the connection's
        // RootFolder when confined). Backslash-separated.
        private string _currentPath = string.Empty;
        private string? _selected;
        private List<string> _dirs = [];
        private bool _busy;
        private string? _error;

        // New-folder inline bar.
        private bool _creating;
        private string _newName = string.Empty;
        private string? _createError;
        private bool _focusNewFolder;
        private ElementReference _newFolderInput;

        private IEnumerable<string> Segments =>
            _currentPath.Length == 0 ? [] : _currentPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // The share-relative root the browse is confined to (the connection's RootFolder when confined,
        // otherwise the share root). _currentPath is always relative to this.
        private string Root => ConfineToRoot ? Normalize(Info.RootFolder) : string.Empty;

        // The label for the root crumb: the connection's root folder when confined to it, else the share.
        private string RootLabel
        {
            get
            {
                var root = Root;
                return ConfineToRoot && root.Length > 0 ? root : Info.Share;
            }
        }

        // Maps a root-relative path to the full share-relative path the connector expects.
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
                _dirs = (await SmbConnector.ListDirectoriesAsync(Info, Absolute(_currentPath))).ToList();
            }
            catch (SmbBrowseException ex)
            {
                _dirs = [];
                _error = ex.Message;
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
            _focusNewFolder = true; // focus the input on the next render
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

            // The new folder's path relative to the browse root (what we select), and its full
            // share-relative path (what the connector creates).
            var relative = _currentPath.Length == 0 ? name : $@"{_currentPath}\{name}";

            _busy = true;
            try
            {
                await SmbConnector.CreateDirectoryAsync(Info, Absolute(relative));
            }
            catch (SmbBrowseException ex)
            {
                _createError = ex.Message;
                return;
            }
            catch (Exception ex)
            {
                _createError = $"Could not create folder: {ex.Message}";
                return;
            }
            finally
            {
                _busy = false;
            }

            _creating = false;
            await LoadAsync();     // re-list so the new folder shows
            _selected = relative;  // and pre-select it (relative to the browse root)
        }

        private string? ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Enter a folder name.";
            }
            if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
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
                    // The element may not be present (race with a re-render) — focus is best-effort.
                }
            }
        }

        private static string Normalize(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').Trim('\\');
    }
}
