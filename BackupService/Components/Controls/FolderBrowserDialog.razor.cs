using BackupService.FileSystem;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Modal that browses folders on the server (drives → subfolders) and returns the
    /// currently-open folder via <see cref="OnSelect"/>.
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

        private IReadOnlyList<string> _roots = [];
        private IReadOnlyList<string> _directories = [];
        private string? _currentPath;

        protected override void OnInitialized()
        {
            _roots = Browser.GetRoots();

            if (!string.IsNullOrWhiteSpace(InitialPath) && Directory.Exists(InitialPath))
            {
                Open(InitialPath);
            }
        }

        private void Open(string path)
        {
            _currentPath = path;
            _directories = Browser.GetDirectories(path);
        }

        private void GoUp()
        {
            if (_currentPath is null)
            {
                return;
            }

            var parent = Browser.GetParent(_currentPath);
            if (parent is null)
            {
                _currentPath = null; // back to the drive list
                _directories = [];
            }
            else
            {
                Open(parent);
            }
        }

        private async Task SelectAsync()
        {
            if (_currentPath is not null)
            {
                await OnSelect.InvokeAsync(_currentPath);
            }
        }

        private static string DisplayName(string path)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }
}
