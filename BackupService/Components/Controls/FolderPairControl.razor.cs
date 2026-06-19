using BackupService.Components.Dialogs;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a profile's list of folder pairs: shows each pair (source/target) with
    /// edit and delete actions, plus an Add button that opens <see cref="FolderPairEditDialog"/>.
    /// Bound to the <see cref="List{T}"/> it mutates in place.
    /// </summary>
    public partial class FolderPairControl : ComponentBase
    {
        [Parameter]
        public List<FolderPairModel> Items { get; set; } = default!;

        private FolderPairModel? _editing;
        private int _editIndex = -1; // -1 when adding a new pair
        private bool _error;

        private void AddNew()
        {
            _editIndex = -1;
            _editing = new FolderPairModel();
        }

        private void Edit(int index)
        {
            _editIndex = index;
            _editing = Clone(Items[index]);
        }

        private void Delete(int index) => Items.RemoveAt(index);

        private void OnEditSaved(FolderPairModel model)
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

        /// <summary>Requires at least one folder pair; surfaces an inline message otherwise.</summary>
        public bool Validate()
        {
            _error = Items.Count == 0;
            StateHasChanged();
            return !_error;
        }

        private static FolderPairModel Clone(FolderPairModel source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            SourceFolder = source.SourceFolder,
            TargetFolder = source.TargetFolder,
            AllowDeletions = source.AllowDeletions,
            IncludeSubFolders = source.IncludeSubFolders,
            OverwriteBehaviour = source.OverwriteBehaviour,
            Includes = FilterEntryModelExtensions.Clone(source.Includes),
            Excludes = FilterEntryModelExtensions.Clone(source.Excludes),
        };
    }

    /// <summary>Editable values for a folder pair within a profile.</summary>
    public sealed class FolderPairModel
    {
        /// <summary>Existing folder pair id, or 0 for a newly added pair.</summary>
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SourceFolder { get; set; } = string.Empty;

        public string TargetFolder { get; set; } = string.Empty;

        public bool AllowDeletions { get; set; }

        public bool IncludeSubFolders { get; set; }

        public OverwriteBehaviour OverwriteBehaviour { get; set; }

        /// <summary>Include rules (empty = back up everything).</summary>
        public List<FilterEntryModel> Includes { get; set; } = [];

        /// <summary>Exclude rules (files and folders left out of the backup).</summary>
        public List<FilterEntryModel> Excludes { get; set; } = [];
    }
}
