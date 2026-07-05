using BackupService.Components.Dialogs;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a profile's list of two-way sync items — the counterpart of <see cref="FolderPairControl"/>,
    /// opening <see cref="TwoWaySyncEditDialog"/>. Bound to the <see cref="List{T}"/> it mutates in place.
    /// </summary>
    public partial class TwoWaySyncControl : ComponentBase
    {
        private const int PageSize = 8;

        [Parameter]
        public List<TwoWaySyncItemModel> Items { get; set; } = default!;

        /// <summary>The profile-level source connection (null = local); the row editor browses against it.</summary>
        [Parameter]
        public int? SourceConnectionId { get; set; }

        /// <summary>The profile-level target connection (null = local); the row editor browses against it.</summary>
        [Parameter]
        public int? TargetConnectionId { get; set; }

        private TwoWaySyncItemModel? _editing;
        private int _editIndex = -1; // -1 when adding a new item
        private bool _error;
        private int _page = 1;

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(Items.Count / (double)PageSize));

        private IEnumerable<(int Index, TwoWaySyncItemModel Item)> PageItems()
        {
            ClampPage();
            var start = (_page - 1) * PageSize;
            for (var i = start; i < Math.Min(start + PageSize, Items.Count); i++)
            {
                yield return (i, Items[i]);
            }
        }

        private void ClampPage() => _page = Math.Clamp(_page, 1, TotalPages);

        private void PreviousPage()
        {
            if (_page > 1)
            {
                _page--;
            }
        }

        private void NextPage()
        {
            if (_page < TotalPages)
            {
                _page++;
            }
        }

        private void AddNew()
        {
            _editIndex = -1;
            _editing = new TwoWaySyncItemModel();
        }

        private void Edit(int index)
        {
            _editIndex = index;
            _editing = Clone(Items[index]);
        }

        private void Delete(int index)
        {
            Items.RemoveAt(index);
            ClampPage();
        }

        private void OnEditSaved(TwoWaySyncItemModel model)
        {
            if (_editIndex >= 0)
            {
                Items[_editIndex] = model;
            }
            else
            {
                Items.Add(model);
                _page = TotalPages;
            }

            _editing = null;
            _error = false;
        }

        /// <summary>Requires at least one item; surfaces an inline message otherwise.</summary>
        public bool Validate()
        {
            _error = Items.Count == 0;
            StateHasChanged();
            return !_error;
        }

        private static TwoWaySyncItemModel Clone(TwoWaySyncItemModel source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            SourceFolder = source.SourceFolder,
            TargetFolder = source.TargetFolder,
            IncludeSubFolders = source.IncludeSubFolders,
            ConflictResolution = source.ConflictResolution,
            PropagateDeletions = source.PropagateDeletions,
            Includes = FilterEntryModelExtensions.Clone(source.Includes),
            Excludes = FilterEntryModelExtensions.Clone(source.Excludes),
        };
    }

    /// <summary>Editable values for a two-way sync item within a profile.</summary>
    public sealed class TwoWaySyncItemModel
    {
        /// <summary>Existing item id, or 0 for a newly added item.</summary>
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SourceFolder { get; set; } = string.Empty;

        public string TargetFolder { get; set; } = string.Empty;

        public bool IncludeSubFolders { get; set; } = true;

        public ConflictResolution ConflictResolution { get; set; } = ConflictResolution.NewerWins;

        public bool PropagateDeletions { get; set; } = true;

        /// <summary>Include rules (empty = sync everything).</summary>
        public List<FilterEntryModel> Includes { get; set; } = [];

        /// <summary>Exclude rules (files and folders left out of the sync).</summary>
        public List<FilterEntryModel> Excludes { get; set; } = [];
    }
}
