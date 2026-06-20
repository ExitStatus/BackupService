using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IInstantSyncProcessor"/>. Copies each changed source path to its rebased
    /// target location (crash-safe, via a dot-prefixed temp then rename — the same idiom as
    /// <see cref="FolderPairSynchronizer"/>) and, when deletions are allowed, mirrors removals. All
    /// filesystem access goes through <see cref="IBackupFileSystem"/> so the logic is unit-testable.
    /// Instant sync is source-authoritative: a changed file always overwrites the target.
    /// </summary>
    public sealed class InstantSyncProcessor(IBackupFileSystem fileSystem) : IInstantSyncProcessor
    {
        public async Task<BackupResult> ProcessBatchAsync(
            InstantSyncItem item,
            IReadOnlyCollection<string> changedPaths,
            IReadOnlyCollection<string> deletedPaths,
            IOperationLogger log,
            CancellationToken cancellationToken)
        {
            var result = new BackupResult();

            foreach (var sourcePath in changedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyChangeAsync(item, sourcePath, log, result);
            }

            if (item.AllowDeletions)
            {
                foreach (var sourcePath in deletedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyDeletionAsync(item, sourcePath, log, result);
                }
            }

            return result;
        }

        private async Task ApplyChangeAsync(InstantSyncItem item, string sourcePath, IOperationLogger log, BackupResult result)
        {
            var destPath = RebaseToTarget(item, sourcePath);

            try
            {
                // The path may have been deleted/renamed away before this batch ran — nothing to do.
                if (fileSystem.DirectoryExists(sourcePath))
                {
                    await EnsureDirectoryAsync(destPath, log, result);
                    return;
                }
                if (!fileSystem.FileExists(sourcePath))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to inspect '{sourcePath}'", ex);
                return;
            }

            var targetDir = Path.GetDirectoryName(destPath)!;
            if (!await EnsureDirectoryAsync(targetDir, log, result))
            {
                return;
            }

            if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, log, result))
            {
                result.Copied++;
                await log.AppendAsync($"Copied '{sourcePath}' -> '{destPath}'");
            }
        }

        private async Task ApplyDeletionAsync(InstantSyncItem item, string sourcePath, IOperationLogger log, BackupResult result)
        {
            var destPath = RebaseToTarget(item, sourcePath);

            try
            {
                if (fileSystem.FileExists(destPath))
                {
                    fileSystem.DeleteFile(destPath);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted '{destPath}' (removed from source)");
                }
                else if (fileSystem.DirectoryExists(destPath))
                {
                    fileSystem.DeleteDirectory(destPath, recursive: true);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted folder '{destPath}' (removed from source)");
                }
                // Nothing at the target — nothing to mirror, nothing logged.
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete '{destPath}'", ex);
            }
        }

        /// <summary>Maps a full source path to the matching target path under the item's target folder.</summary>
        private static string RebaseToTarget(InstantSyncItem item, string sourcePath)
        {
            var relative = Path.GetRelativePath(item.SourceFolder, sourcePath);
            return Path.Combine(item.TargetFolder, relative);
        }

        /// <summary>Creates <paramref name="directory"/> (and logs it) if missing. Returns false on failure.</summary>
        private async Task<bool> EnsureDirectoryAsync(string directory, IOperationLogger log, BackupResult result)
        {
            try
            {
                if (!fileSystem.DirectoryExists(directory))
                {
                    fileSystem.CreateDirectory(directory);
                    await log.AppendAsync($"Created folder '{directory}'");
                }
                return true;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to create target folder '{directory}'", ex);
                return false;
            }
        }

        /// <summary>
        /// Crash-safe copy: writes to a dot-prefixed temp in the target folder, then (on success)
        /// removes any existing destination and renames the temp onto it. On any failure the temp is
        /// removed so a partial/temp file is never left behind; the error is logged. Returns success.
        /// </summary>
        private async Task<bool> CopyThroughTempAsync(string source, string dest, string targetDir, IOperationLogger log, BackupResult result)
        {
            var tempPath = Path.Combine(targetDir, "." + Path.GetFileName(dest) + ".tmp");
            try
            {
                fileSystem.CopyFile(source, tempPath, overwrite: true); // fresh temp, clobbering any stale one
                if (fileSystem.FileExists(dest))
                {
                    fileSystem.DeleteFile(dest);
                }
                fileSystem.MoveFile(tempPath, dest, overwrite: false);
                result.BytesCopied += TrySize(source);
                return true;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(tempPath);
                if (FileLock.IsLockViolation(ex))
                {
                    // The file is locked by another process — skip it this run (non-fatal warning).
                    result.Warnings++;
                    await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{source}' — in use by another process (locked)");
                }
                else
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to copy '{source}' -> '{dest}'", ex);
                }
                return false;
            }
        }

        // Best-effort file size for the bytes-copied stat — never let a stats read fail the copy.
        private long TrySize(string path)
        {
            try
            {
                return fileSystem.GetFileSize(path);
            }
            catch
            {
                return 0;
            }
        }

        private void TryDeleteTemp(string tempPath)
        {
            try
            {
                if (fileSystem.FileExists(tempPath))
                {
                    fileSystem.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup — never let a secondary failure mask the original error.
            }
        }
    }
}
