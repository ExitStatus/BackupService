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

        [Parameter]
        public EventCallback<InstantSyncItemModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private string? _browseFor; // "source" or "target"
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

        private string? CurrentBrowsePath =>
            _browseFor == "source" ? Model.SourceFolder : Model.TargetFolder;

        private void BrowseSource() => _browseFor = "source";

        private void BrowseTarget() => _browseFor = "target";

        private void OnFolderSelected(string path)
        {
            if (_browseFor == "source")
            {
                Model.SourceFolder = path;
            }
            else if (_browseFor == "target")
            {
                Model.TargetFolder = path;
            }

            _browseFor = null;
        }

        private async Task SaveAsync()
        {
            _nameError = string.IsNullOrWhiteSpace(Model.Name);
            _sourceError = string.IsNullOrWhiteSpace(Model.SourceFolder);
            _targetError = string.IsNullOrWhiteSpace(Model.TargetFolder);
            _debounceError = Model.DebounceMilliseconds < 0;

            if (_nameError || _sourceError || _targetError || _debounceError)
            {
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
