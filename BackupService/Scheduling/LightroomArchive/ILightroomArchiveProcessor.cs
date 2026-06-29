using BackupService.Database;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Applies one debounced batch of source changes for a <see cref="LightroomArchiveItem"/> to its target:
    /// copies each changed source path (rebased under the target) and, for each copied file, pulls the matching
    /// raw sidecar(s) from the profile's Lightroom folder into a RAW sub-folder beside the copy. When the item
    /// allows deletions, removals are mirrored (including the matching raws). The target may be local or on a
    /// connection — it is resolved through <see cref="FileSystem.IEndpointFileSystemFactory"/>, so the same
    /// path serves both. The testable file engine behind <c>LightroomArchiveWatcherService</c>.
    /// </summary>
    public interface ILightroomArchiveProcessor
    {
        /// <summary>
        /// Copies each path in <paramref name="changedPaths"/> (full local source paths) to the target plus its
        /// matching raws, and — when <see cref="LightroomArchiveItem.AllowDeletions"/> is set — deletes the
        /// target (and matching raws) of each path in <paramref name="deletedPaths"/>. Operations performed are
        /// logged to <paramref name="log"/>; the returned <see cref="BackupResult"/> carries the counts.
        /// <paramref name="progress"/> (optional) receives one report per processed changed path.
        /// </summary>
        Task<BackupResult> ProcessBatchAsync(
            LightroomArchiveItem item,
            int? targetConnectionId,
            LightroomArchiveSettings settings,
            IReadOnlyCollection<string> changedPaths,
            IReadOnlyCollection<string> deletedPaths,
            IOperationLogger log,
            IProgress<int>? progress,
            CancellationToken cancellationToken);
    }
}
