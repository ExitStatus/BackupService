using BackupService.Database;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Synchronises a single <see cref="FolderPair"/>'s source folder into its target folder,
    /// applying the pair's comparison/overwrite/deletion rules and logging every operation performed
    /// to the run's <see cref="IOperationLogger"/>. Returns aggregate counts; per-file/folder errors
    /// are logged (and counted) but do not abort the run.
    /// </summary>
    public interface IFolderPairSynchronizer
    {
        /// <summary>
        /// Synchronises the pair. <paramref name="fileProgress"/>, when supplied, is reported <c>1</c> for each
        /// in-scope source file after it's handled (copied/updated/skipped) — pair it with
        /// <see cref="CountFilesAsync"/> for a percentage.
        /// </summary>
        Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null);

        /// <summary>
        /// Counts the in-scope source files the sync would process (same include/exclude + IncludeSubFolders
        /// rules as <see cref="SyncAsync"/>), via a cheap source-only walk — the denominator for progress.
        /// </summary>
        Task<int> CountFilesAsync(FolderPair pair, CancellationToken cancellationToken);
    }
}
