using BackupService.Components.Controls;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single two-way sync item — the counterpart of
    /// <see cref="OneWaySyncEditDialog"/>, with a conflict-resolution dropdown and a propagate-deletions toggle
    /// instead of the one-way overwrite/allow-deletions options.
    /// </summary>
    public partial class TwoWaySyncEditDialog : ComponentBase
    {
        [Parameter]
        public TwoWaySyncItemModel Model { get; set; } = default!;

        /// <summary>Profile-level source connection (null = local); the source folder Browse uses it.</summary>
        [Parameter]
        public int? SourceConnectionId { get; set; }

        /// <summary>Profile-level target connection (null = local); the target folder Browse uses it.</summary>
        [Parameter]
        public int? TargetConnectionId { get; set; }

        [Parameter]
        public EventCallback<TwoWaySyncItemModel> OnSave { get; set; }

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
            _sourceError = SourceConnectionId is null && string.IsNullOrWhiteSpace(Model.SourceFolder);
            _targetError = TargetConnectionId is null && string.IsNullOrWhiteSpace(Model.TargetFolder);

            if (_nameError || _sourceError || _targetError)
            {
                _activeTab = "detail";
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
