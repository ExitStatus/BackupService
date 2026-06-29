using BackupService.Database;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Builds one archive for an <see cref="ArchiveSyncItem"/> and applies its retention policy. The
    /// testable file engine behind <see cref="ArchiveSyncHandler"/>: zips the source into a local temp
    /// folder, crash-safe copies it into the target as a timestamped (and, for GFS, level-tagged) ZIP,
    /// then prunes/promotes older archives.
    /// </summary>
    public interface IArchiveSyncProcessor
    {
        /// <summary>
        /// Creates a single archive of <paramref name="item"/>'s source and applies retention.
        /// <paramref name="runIndex"/> is the 1-based count of archives created for this item
        /// (it drives the grandfather-father-son promotion cadence). <paramref name="timestamp"/> is
        /// stamped into the archive's file name. Operations performed are logged to
        /// <paramref name="log"/>; the returned <see cref="BackupResult"/> carries the counts
        /// (<c>Copied</c> = archives created, <c>Deleted</c> = archives pruned).
        /// <paramref name="progress"/>, when supplied, reports this item's completion fraction (0..1):
        /// the first 75% tracks files added to the zip, the final 25% tracks bytes copied to the target.
        /// </summary>
        Task<BackupResult> CreateArchiveAsync(
            ArchiveSyncItem item,
            int? sourceConnectionId,
            int? targetConnectionId,
            long runIndex,
            DateTime timestamp,
            IOperationLogger log,
            CancellationToken cancellationToken,
            IProgress<double>? progress = null);
    }
}
