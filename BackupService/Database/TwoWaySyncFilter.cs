using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One include/exclude rule for a <see cref="TwoWaySyncItem"/>. <see cref="Direction"/> selects the list
    /// (Includes restrict the sync to matching files; Excludes remove them), <see cref="Kind"/> selects what the
    /// <see cref="Pattern"/> matches (a file name or a folder subtree). Matching is name-only and case-insensitive
    /// — see <see cref="FileSystem.BackupFilter"/>.
    /// </summary>
    public class TwoWaySyncFilter
    {
        public int Id { get; set; }

        public int TwoWaySyncItemId { get; set; }

        public TwoWaySyncItem? TwoWaySyncItem { get; set; }

        public FilterDirection Direction { get; set; }

        public FilterKind Kind { get; set; }

        [MaxLength(512)]
        public required string Pattern { get; set; }
    }
}
