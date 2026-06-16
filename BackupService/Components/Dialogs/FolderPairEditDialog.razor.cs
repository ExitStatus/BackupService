using BackupService.Components.Controls;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single folder pair (source/target + Browse, watch flag).
    /// Hosts the folder browser. Edits the <see cref="Model"/> instance passed in (the parent
    /// supplies a working copy) and returns it via <see cref="OnSave"/> when valid.
    /// </summary>
    public partial class FolderPairEditDialog : ComponentBase
    {
        [Parameter]
        public FolderPairModel Model { get; set; } = default!;

        [Parameter]
        public EventCallback<FolderPairModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private string? _browseFor; // "source" or "target"
        private bool _nameError;
        private bool _sourceError;
        private bool _targetError;

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

            if (_nameError || _sourceError || _targetError)
            {
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
