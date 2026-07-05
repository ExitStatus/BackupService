using BackupService.Database;
using BackupService.Logging;

namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>
    /// Reconciles a single <see cref="TwoWaySyncItem"/>'s two folders in <b>both</b> directions, using a
    /// persisted baseline (see <see cref="ITwoWaySyncStateStore"/>) to distinguish a deletion from a new file and
    /// to detect conflicts. The two sides may live on different filesystems (local or a remote connection), each
    /// resolved via <see cref="FileSystem.IEndpointFileSystemFactory"/>. Every operation performed is logged;
    /// per-file errors are logged/counted but do not abort the run.
    /// </summary>
    public interface ITwoWaySyncEngine
    {
        /// <summary>
        /// Reconciles the item. Source/target connections are profile-level (null = local). <paramref name="fileProgress"/>
        /// is reported <c>1</c> per in-scope file handled — pair it with <see cref="CountFilesAsync"/> for a percentage.
        /// </summary>
        Task<BackupResult> SyncAsync(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null, Action<string?>? onCurrentFile = null);

        /// <summary>Counts the distinct in-scope files across <b>both</b> sides — the denominator for progress.</summary>
        Task<int> CountFilesAsync(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId, CancellationToken cancellationToken);
    }
}
