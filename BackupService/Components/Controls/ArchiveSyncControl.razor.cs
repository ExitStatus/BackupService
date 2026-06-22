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
        private const int PageSize = 8;

        [Parameter]
        public List<ArchiveSyncItemModel> Items { get; set; } = default!;

        private ArchiveSyncItemModel? _editing;
        private int _editIndex = -1; // -1 when adding a new item
        private bool _error;
        private int _page = 1;

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(Items.Count / (double)PageSize));

        /// <summary>The archives (with their absolute index) shown on the current page.</summary>
        private IEnumerable<(int Index, ArchiveSyncItemModel Item)> PageItems()
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
            _editing = new ArchiveSyncItemModel();
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

        private void OnEditSaved(ArchiveSyncItemModel model)
        {
            if (_editIndex >= 0)
            {
                Items[_editIndex] = model;
            }
            else
            {
                Items.Add(model);
                _page = TotalPages; // jump to the page holding the new archive
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
            SourceConnectionId = source.SourceConnectionId,
            TargetConnectionId = source.TargetConnectionId,
            FileName = source.FileName,
            IncludeSubFolders = source.IncludeSubFolders,
            OnlyCopyOnChange = source.OnlyCopyOnChange,
            CompressionLevel = source.CompressionLevel,
            RetentionMode = source.RetentionMode,
            RetentionCount = source.RetentionCount,
            MaxLevels = source.MaxLevels,
            Includes = FilterEntryModelExtensions.Clone(source.Includes),
            Excludes = FilterEntryModelExtensions.Clone(source.Excludes),
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

        /// <summary>When set, the source is on this connection (staged to local temp before zipping).</summary>
        public int? SourceConnectionId { get; set; }

        /// <summary>When set, the target is on this connection.</summary>
        public int? TargetConnectionId { get; set; }

        public string FileName { get; set; } = string.Empty;

        public bool IncludeSubFolders { get; set; }

        /// <summary>When true, only create a new archive when the source content has changed.</summary>
        public bool OnlyCopyOnChange { get; set; }

        /// <summary>How hard to compress the ZIP; defaults to Optimal.</summary>
        public ArchiveCompressionLevel CompressionLevel { get; set; } = ArchiveCompressionLevel.Optimal;

        public ArchiveRetentionMode RetentionMode { get; set; }

        /// <summary>Archives kept (total for Keep-last-N, per level for GFS); defaults to 5.</summary>
        public int RetentionCount { get; set; } = 5;

        /// <summary>GFS level count; defaults to 3 (son/father/grandfather).</summary>
        public int MaxLevels { get; set; } = 3;

        /// <summary>Include rules (empty = archive everything).</summary>
        public List<FilterEntryModel> Includes { get; set; } = [];

        /// <summary>Exclude rules (files and folders left out of the archive).</summary>
        public List<FilterEntryModel> Excludes { get; set; } = [];
    }
}
