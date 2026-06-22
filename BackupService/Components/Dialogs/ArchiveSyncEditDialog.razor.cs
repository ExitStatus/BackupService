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

        private static readonly IReadOnlyList<TabBar.TabItem> _tabs =
        [
            new("location", "Location"),
            new("retention", "Retention"),
            new("archive", "Archive"),
            new("includes", "File Includes"),
            new("excludes", "Excludes"),
        ];

        private string _activeTab = "location";
        private bool _nameError;
        private bool _sourceError;
        private bool _targetError;
        private bool _fileNameError;
        private bool _countError;
        private bool _levelsError;
        private bool _passwordError;

        private bool IsGfs => Model.RetentionMode == ArchiveRetentionMode.GrandfatherFatherSon;

        private string RetentionCountLabel => IsGfs ? "Archives kept per level" : "Archives to keep";

        private string FileNamePreview =>
            string.IsNullOrWhiteSpace(Model.FileName) ? "MyBackup" : Model.FileName;

        private async Task SaveAsync()
        {
            _nameError = string.IsNullOrWhiteSpace(Model.Name);
            // A remote side may legitimately be the connection root (empty), so only require a path locally.
            _sourceError = Model.SourceConnectionId is null && string.IsNullOrWhiteSpace(Model.SourceFolder);
            _targetError = Model.TargetConnectionId is null && string.IsNullOrWhiteSpace(Model.TargetFolder);
            _fileNameError = string.IsNullOrWhiteSpace(Model.FileName);
            _countError = Model.RetentionCount < 1;
            _levelsError = IsGfs && Model.MaxLevels < 1;
            // A password is required when protecting a new archive (or one without a stored password);
            // on an existing protected archive a blank box keeps the stored password.
            _passwordError = Model.PasswordProtect && !Model.HasExistingPassword && string.IsNullOrWhiteSpace(Model.Password);

            if (_nameError || _sourceError || _targetError)
            {
                _activeTab = "location"; // name + source/target live on the Location tab
                return;
            }
            if (_countError || _levelsError)
            {
                _activeTab = "retention"; // the keep-count and max-levels live on the Retention tab
                return;
            }
            if (_fileNameError || _passwordError)
            {
                _activeTab = "archive"; // the file name and password live on the Archive tab
                return;
            }

            await OnSave.InvokeAsync(Model);
        }
    }
}
