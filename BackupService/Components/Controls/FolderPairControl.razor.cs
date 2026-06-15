using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a single folder pair (source/target folders, watch flag). Hosts the
    /// folder browser sub-dialog. Shown by the Create Profile dialog when the
    /// <c>FolderPair</c> profile type is selected. The schedule is a profile-level field
    /// collected by the parent dialog, not here.
    /// </summary>
    public partial class FolderPairControl : ComponentBase
    {
        [Parameter]
        public FolderPairModel Model { get; set; } = default!;

        private string? _browseFor; // "source" or "target"
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

        /// <summary>
        /// Validates the required fields, surfacing inline messages. Returns true when valid.
        /// </summary>
        public bool Validate()
        {
            _sourceError = string.IsNullOrWhiteSpace(Model.SourceFolder);
            _targetError = string.IsNullOrWhiteSpace(Model.TargetFolder);
            StateHasChanged();
            return !_sourceError && !_targetError;
        }
    }

    /// <summary>Editable values for a folder pair within a profile.</summary>
    public sealed class FolderPairModel
    {
        public string SourceFolder { get; set; } = string.Empty;

        public string TargetFolder { get; set; } = string.Empty;

        public bool WatchFolder { get; set; }
    }
}
