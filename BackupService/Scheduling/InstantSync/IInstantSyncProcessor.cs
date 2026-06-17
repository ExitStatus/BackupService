using BackupService.Database;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Applies one debounced batch of source changes for an <see cref="InstantSyncItem"/> to its target:
    /// copies the changed source paths (rebased under the target) and, when the item allows deletions,
    /// removes the targets of deleted source paths. The testable file engine behind
    /// <see cref="InstantSyncWatcherService"/>.
    /// </summary>
    public interface IInstantSyncProcessor
    {
        /// <summary>
        /// Copies each path in <paramref name="changedPaths"/> (full source paths) to the target and,
        /// when <see cref="InstantSyncItem.AllowDeletions"/> is set, deletes the target of each path in
        /// <paramref name="deletedPaths"/>. Operations performed are logged to <paramref name="log"/>;
        /// the returned <see cref="BackupResult"/> carries the counts.
        /// </summary>
        Task<BackupResult> ProcessBatchAsync(
            InstantSyncItem item,
            IReadOnlyCollection<string> changedPaths,
            IReadOnlyCollection<string> deletedPaths,
            IOperationLogger log,
            CancellationToken cancellationToken);
    }
}
