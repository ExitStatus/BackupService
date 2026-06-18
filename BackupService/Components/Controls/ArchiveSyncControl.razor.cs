using BackupService.Components.Dialogs;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a profile's list of archives: shows each archive with edit and delete actions,
    /// plus an Add button that opens <see cref="ArchiveSyncEditDialog"/>. Bound to the
    /// <see cref="List{T}"/> it mutates in place. The archive-sync counterpart of
    /// <see cref="FolderPairControl"/>.
    /// </summary>
    public partial class ArchiveSyncControl : ComponentBase
    {
        [Parameter]
        public List<ArchiveSyncItemModel> Items { get; set; } = default!;

        private ArchiveSyncItemModel? _editing;
        private int _editIndex = -1; // -1 when adding a new item
        private bool _error;

        private void AddNew()
        {
            _editIndex = -1;
            _editing = new ArchiveSyncItemModel();
        }

        private void Edit(int index)
        {
            _editIndex = index;
            _editing = Clone(Items[index]);
        }

        private void Delete(int index) => Items.RemoveAt(index);

        private void OnEditSaved(ArchiveSyncItemModel model)
        {
            if (_editIndex >= 0)
            {
                Items[_editIndex] = model;
            }
            else
            {
                Items.Add(model);
            }

            _editing = null;
            _error = false;
        }

        /// <summary>Requires at least one archive; surfaces an inline message otherwise.</summary>
        public bool Validate()
        {
            _error = Items.Count == 0;
            StateHasChanged();
            return !_error;
        }

        private static ArchiveSyncItemModel Clone(ArchiveSyncItemModel source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            SourceFolder = source.SourceFolder,
            TargetFolder = source.TargetFolder,
            FileName = source.FileName,
            IncludeSubFolders = source.IncludeSubFolders,
            RetentionMode = source.RetentionMode,
            RetentionCount = source.RetentionCount,
            MaxLevels = source.MaxLevels,
        };
    }

    /// <summary>Editable values for an archive within a profile.</summary>
    public sealed class ArchiveSyncItemModel
    {
        /// <summary>Existing archive item id, or 0 for a newly added one.</summary>
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SourceFolder { get; set; } = string.Empty;

        public string TargetFolder { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public bool IncludeSubFolders { get; set; }

        public ArchiveRetentionMode RetentionMode { get; set; }

        /// <summary>Archives kept (total for Keep-last-N, per level for GFS); defaults to 5.</summary>
        public int RetentionCount { get; set; } = 5;

        /// <summary>GFS level count; defaults to 3 (son/father/grandfather).</summary>
        public int MaxLevels { get; set; } = 3;
    }
}
