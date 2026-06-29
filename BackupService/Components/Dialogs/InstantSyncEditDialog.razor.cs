using BackupService.Components.Controls;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single instant sync item (source/target + Browse, debounce,
    /// include-subfolders and allow-deletions flags). Hosts the folder browser. Edits the
    /// <see cref="Model"/> instance passed in (the parent supplies a working copy) and returns it via
    /// <see cref="OnSave"/> when valid. The instant-sync counterpart of <see cref="FolderPairEditDialog"/>.
    /// </summary>
    public partial class InstantSyncEditDialog : ComponentBase
    {
        [Parameter]
        public InstantSyncItemModel Model { get; set; } = default!;

        /// <summary>Profile-level target connection (null = local); the target folder Browse uses it.</summary>
        [Parameter]
        public int? TargetConnectionId { get; set; }

        [Parameter]
        public EventCallback<InstantSyncItemModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private bool _nameError;
        private bool _sourceError;
        private bool _targetError;
        private bool _debounceError;

        /// <summary>The debounce window shown/edited in seconds, stored on the model as milliseconds.</summary>
        private double DebounceSeconds
        {
            get => Model.DebounceMilliseconds / 1000.0;
            set => Model.DebounceMilliseconds = (int)Math.Round(value * 1000);
        }

        private async Task SaveAsync()
        {
            _nameError = string.IsNullOrWhiteSpace(Model.Name);
            // The source is always a local folder (a remote source can't be watched live).
            _sourceError = string.IsNullOrWhiteSpace(Model.SourceFolder);
            // A remote target may legitimately be the connection root (empty), so only require a path locally.
            _targetError = TargetConnectionId is null && string.IsNullOrWhiteSpace(Model.TargetFolder);
            _debounceError = Model.DebounceMilliseconds < 0;

            if (_nameError || _sourceError || _targetError || _debounceError)
            {
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
