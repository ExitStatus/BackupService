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
        Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken);
    }
}
