using BackupService.Enumerations;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A single include/exclude entry as edited in the UI (one row of a <see cref="FilterListEditor"/>).
    /// <see cref="Kind"/> is <see cref="FilterKind.File"/> for both "specific file" and "wildcard"
    /// includes (the distinction is derived from whether <see cref="Pattern"/> contains a wildcard);
    /// excludes use <see cref="FilterKind.File"/> or <see cref="FilterKind.Folder"/>.
    /// </summary>
    public sealed class FilterEntryModel
    {
        public int Id { get; set; }

        public FilterKind Kind { get; set; }

        public string Pattern { get; set; } = string.Empty;
    }

    /// <summary>Helpers for working with lists of <see cref="FilterEntryModel"/>.</summary>
    public static class FilterEntryModelExtensions
    {
        /// <summary>A deep copy of a filter list, so an edit dialog's working copy can't mutate the original.</summary>
        public static List<FilterEntryModel> Clone(IEnumerable<FilterEntryModel> source) =>
            source.Select(e => new FilterEntryModel { Id = e.Id, Kind = e.Kind, Pattern = e.Pattern }).ToList();
    }
}
