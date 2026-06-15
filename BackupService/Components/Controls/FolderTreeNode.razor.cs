using BackupService.FileSystem;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A single node in the folder browser's left navigation tree. Expands lazily,
    /// loading its child folders from <see cref="IFolderBrowser"/> on first open, and
    /// renders each child as a nested <see cref="FolderTreeNode"/>.
    /// </summary>
    public partial class FolderTreeNode : ComponentBase
    {
        [Inject]
        private IFolderBrowser Browser { get; set; } = default!;

        [Parameter, EditorRequired]
        public string Path { get; set; } = default!;

        [Parameter, EditorRequired]
        public string Label { get; set; } = default!;

        [Parameter]
        public bool IsDrive { get; set; }

        [Parameter]
        public int Depth { get; set; }

        [Parameter]
        public string? SelectedPath { get; set; }

        [Parameter]
        public EventCallback<string> OnNavigate { get; set; }

        private bool _expanded;
        private bool _loaded;
        private bool _hasNoChildren;
        private IReadOnlyList<FolderEntry> _children = [];

        private bool IsSelected => string.Equals(Path, SelectedPath, StringComparison.OrdinalIgnoreCase);

        private void Toggle()
        {
            if (!_loaded)
            {
                _children = Browser.GetDirectories(Path);
                _hasNoChildren = _children.Count == 0;
                _loaded = true;
            }

            _expanded = !_expanded && !_hasNoChildren;
        }

        private async Task NavigateAsync() => await OnNavigate.InvokeAsync(Path);
    }
}
