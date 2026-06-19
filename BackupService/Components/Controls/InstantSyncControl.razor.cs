using BackupService.Components.Dialogs;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a profile's list of instant sync items: shows each item (source/target) with
    /// edit and delete actions, plus an Add button that opens <see cref="InstantSyncEditDialog"/>.
    /// Bound to the <see cref="List{T}"/> it mutates in place. The instant-sync counterpart of
    /// <see cref="FolderPairControl"/>.
    /// </summary>
    public partial class InstantSyncControl : ComponentBase
    {
        private const int PageSize = 8;

        [Parameter]
        public List<InstantSyncItemModel> Items { get; set; } = default!;

        private InstantSyncItemModel? _editing;
        private int _editIndex = -1; // -1 when adding a new item
        private bool _error;
        private int _page = 1;

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(Items.Count / (double)PageSize));

        /// <summary>The instant sync items (with their absolute index) shown on the current page.</summary>
        private IEnumerable<(int Index, InstantSyncItemModel Item)> PageItems()
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
            _editing = new InstantSyncItemModel();
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

        private void OnEditSaved(InstantSyncItemModel model)
        {
            if (_editIndex >= 0)
            {
                Items[_editIndex] = model;
            }
            else
            {
                Items.Add(model);
                _page = TotalPages; // jump to the page holding the new item
            }

            _editing = null;
            _error = false;
        }

        /// <summary>Requires at least one instant sync item; surfaces an inline message otherwise.</summary>
        public bool Validate()
        {
            _error = Items.Count == 0;
            StateHasChanged();
            return !_error;
        }

        private static InstantSyncItemModel Clone(InstantSyncItemModel source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            SourceFolder = source.SourceFolder,
            TargetFolder = source.TargetFolder,
            DebounceMilliseconds = source.DebounceMilliseconds,
            IncludeSubFolders = source.IncludeSubFolders,
            AllowDeletions = source.AllowDeletions,
        };
    }

    /// <summary>Editable values for an instant sync item within a profile.</summary>
    public sealed class InstantSyncItemModel
    {
        /// <summary>Existing instant sync item id, or 0 for a newly added one.</summary>
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string SourceFolder { get; set; } = string.Empty;

        public string TargetFolder { get; set; } = string.Empty;

        /// <summary>Debounce window in milliseconds; defaults to 5 seconds.</summary>
        public int DebounceMilliseconds { get; set; } = 5000;

        public bool IncludeSubFolders { get; set; }

        public bool AllowDeletions { get; set; }
    }
}
