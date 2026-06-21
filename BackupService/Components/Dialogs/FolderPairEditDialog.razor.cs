using BackupService.Components.Controls;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single folder pair. Each side (source/target) is edited by a
    /// <see cref="ConnectionLocationField"/> — "this machine" (local) or one of the configured connections,
    /// with a connection-aware Browse. Edits the <see cref="Model"/> instance passed in and returns it via
    /// <see cref="OnSave"/> when valid.
    /// </summary>
    public partial class FolderPairEditDialog : ComponentBase
    {
        [Parameter]
        public FolderPairModel Model { get; set; } = default!;

        [Parameter]
        public EventCallback<FolderPairModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private static readonly IReadOnlyList<TabBar.TabItem> _tabs =
        [
            new("detail", "Detail"),
            new("includes", "File Includes"),
            new("excludes", "Excludes"),
        ];

        private string _activeTab = "detail";
        private bool _nameError;
        private bool _sourceError;
        private bool _targetError;

        private async Task SaveAsync()
        {
            _nameError = string.IsNullOrWhiteSpace(Model.Name);
            // A remote side may legitimately be the connection root (empty), so only require a path locally.
            _sourceError = Model.SourceConnectionId is null && string.IsNullOrWhiteSpace(Model.SourceFolder);
            _targetError = Model.TargetConnectionId is null && string.IsNullOrWhiteSpace(Model.TargetFolder);

            if (_nameError || _sourceError || _targetError)
            {
                _activeTab = "detail"; // the required fields live on the Detail tab — show it
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
