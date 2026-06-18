using BackupService.Components.Controls;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal for adding or editing a single archive (source/target + Browse, file name,
    /// include-subfolders flag, and the retention policy). Hosts the folder browser. Edits the
    /// <see cref="Model"/> instance passed in (the parent supplies a working copy) and returns it via
    /// <see cref="OnSave"/> when valid. The archive-sync counterpart of <see cref="FolderPairEditDialog"/>.
    /// </summary>
    public partial class ArchiveSyncEditDialog : ComponentBase
    {
        [Parameter]
        public ArchiveSyncItemModel Model { get; set; } = default!;

        [Parameter]
        public EventCallback<ArchiveSyncItemModel> OnSave { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private string? _browseFor; // "source" or "target"
        private bool _nameError;
        private bool _sourceError;
        private bool _targetError;
        private bool _fileNameError;
        private bool _countError;
        private bool _levelsError;

        private bool IsGfs => Model.RetentionMode == ArchiveRetentionMode.GrandfatherFatherSon;

        private string RetentionCountLabel => IsGfs ? "Archives kept per level" : "Archives to keep";

        private string FileNamePreview =>
            string.IsNullOrWhiteSpace(Model.FileName) ? "MyBackup" : Model.FileName;

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
            _fileNameError = string.IsNullOrWhiteSpace(Model.FileName);
            _countError = Model.RetentionCount < 1;
            _levelsError = IsGfs && Model.MaxLevels < 1;

            if (_nameError || _sourceError || _targetError || _fileNameError || _countError || _levelsError)
            {
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
