using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="ILightroomArchiveProcessor"/>. Endpoint-aware (modelled on
    /// <see cref="FolderPairSynchronizer"/>'s cross-filesystem copy rather than the local-only
    /// <see cref="InstantSyncProcessor"/>): the source and Lightroom catalog are always local, while the
    /// target is resolved through <see cref="IEndpointFileSystemFactory"/> so a copy streams from the local
    /// filesystem into a crash-safe temp on the target filesystem (local or a remote connection). Each copied
    /// file's matching raw sidecar(s) are pulled from the Lightroom folder into a RAW sub-folder beside it.
    /// </summary>
    public sealed class LightroomArchiveProcessor(IEndpointFileSystemFactory endpointFactory, IBackupFileSystem localFileSystem) : ILightroomArchiveProcessor
    {
        public async Task<BackupResult> ProcessBatchAsync(
            LightroomArchiveItem item,
            LightroomArchiveSettings settings,
            IReadOnlyCollection<string> changedPaths,
            IReadOnlyCollection<string> deletedPaths,
            IOperationLogger log,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var result = new BackupResult();

            // Build the basename -> raw paths index once per batch so a big flush doesn't re-walk the catalog.
            var rawIndex = BuildRawIndex(settings);

            var target = await endpointFactory.ResolveAsync(item.TargetConnectionId, item.TargetFolder, cancellationToken);
            try
            {
                var ctx = new Ctx(localFileSystem, target.FileSystem, item, target.BasePath, settings, rawIndex);

                foreach (var sourcePath in changedPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await ApplyChangeAsync(ctx, sourcePath, log, result, cancellationToken);
                    }
                    finally
                    {
                        progress?.Report(1);
                    }
                }

                if (item.AllowDeletions)
                {
                    foreach (var sourcePath in deletedPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ApplyDeletionAsync(ctx, sourcePath, log, result);
                    }
                }
            }
            finally
            {
                target.Session.Dispose();
            }

            return result;
        }

        // Maps a full local source path to the matching path under the resolved target base path.
        private string RebaseToTarget(Ctx ctx, string sourcePath)
        {
            var relative = Path.GetRelativePath(ctx.Item.SourceFolder, sourcePath);
            return relative == "." ? ctx.TargetBase : Path.Combine(ctx.TargetBase, relative);
        }

        private async Task ApplyChangeAsync(Ctx ctx, string sourcePath, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var destPath = RebaseToTarget(ctx, sourcePath);

            try
            {
                // A created/renamed directory: just mirror the folder. (No raw scan for a directory.)
                if (ctx.SourceFs.DirectoryExists(sourcePath))
                {
                    await EnsureDirectoryAsync(ctx, destPath, log, result);
                    return;
                }
                // The path may have been deleted/renamed away before this batch ran — nothing to do.
                if (!ctx.SourceFs.FileExists(sourcePath))
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
            if (!await EnsureDirectoryAsync(ctx, targetDir, log, result))
            {
                return;
            }

            // Copy the photo itself (copy-if-missing-or-changed), then pull its matching raw sidecar(s).
            await CopyIfChangedAsync(ctx, sourcePath, destPath, targetDir, log, result, ct);
            await CopyMatchingRawsAsync(ctx, sourcePath, targetDir, log, result, ct);
        }

        /// <summary>Copies the matching raw sidecar(s) for <paramref name="sourcePath"/> into the RAW sub-folder of its target directory.</summary>
        private async Task CopyMatchingRawsAsync(Ctx ctx, string sourcePath, string targetDir, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(baseName) || !ctx.RawIndex.TryGetValue(baseName, out var rawPaths))
            {
                return;
            }

            var rawDir = Path.Combine(targetDir, ctx.Settings.RawFolderName);
            var ensured = false;

            foreach (var rawPath in rawPaths)
            {
                ct.ThrowIfCancellationRequested();

                // Defer creating the RAW folder until there's actually a raw to copy.
                if (!ensured)
                {
                    if (!await EnsureDirectoryAsync(ctx, rawDir, log, result))
                    {
                        return;
                    }
                    ensured = true;
                }

                var rawDest = Path.Combine(rawDir, Path.GetFileName(rawPath));
                await CopyIfChangedAsync(ctx, rawPath, rawDest, rawDir, log, result, ct);
            }
        }

        private async Task ApplyDeletionAsync(Ctx ctx, string sourcePath, IOperationLogger log, BackupResult result)
        {
            var destPath = RebaseToTarget(ctx, sourcePath);

            try
            {
                if (ctx.TargetFs.FileExists(destPath))
                {
                    ctx.TargetFs.DeleteFile(destPath);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted '{destPath}' (removed from source)");

                    // Mirror the raw sidecar(s) for this file from the RAW folder beside it.
                    await DeleteMatchingRawsAsync(ctx, sourcePath, Path.GetDirectoryName(destPath)!, log, result);
                }
                else if (ctx.TargetFs.DirectoryExists(destPath))
                {
                    ctx.TargetFs.DeleteDirectory(destPath, recursive: true);
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

        /// <summary>Removes any file in the target RAW folder whose basename matches the deleted source file and whose extension is a raw format.</summary>
        private async Task DeleteMatchingRawsAsync(Ctx ctx, string sourcePath, string targetDir, IOperationLogger log, BackupResult result)
        {
            var rawDir = Path.Combine(targetDir, ctx.Settings.RawFolderName);
            if (!ctx.TargetFs.DirectoryExists(rawDir))
            {
                return;
            }

            var baseName = Path.GetFileNameWithoutExtension(sourcePath);

            IReadOnlyList<string> rawFiles;
            try
            {
                rawFiles = ctx.TargetFs.GetFiles(rawDir);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access RAW folder '{rawDir}'", ex);
                return;
            }

            foreach (var rawFile in rawFiles)
            {
                if (!ctx.Settings.RawExtensions.Contains(Path.GetExtension(rawFile)) ||
                    !string.Equals(Path.GetFileNameWithoutExtension(rawFile), baseName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    ctx.TargetFs.DeleteFile(rawFile);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted raw '{rawFile}' (source removed)");
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to delete raw '{rawFile}'", ex);
                }
            }
        }

        /// <summary>Creates <paramref name="directory"/> on the target (and logs it) if missing. Returns false on failure.</summary>
        private static async Task<bool> EnsureDirectoryAsync(Ctx ctx, string directory, IOperationLogger log, BackupResult result)
        {
            try
            {
                if (!ctx.TargetFs.DirectoryExists(directory))
                {
                    ctx.TargetFs.CreateDirectory(directory);
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
        /// Copies <paramref name="source"/> (local) to <paramref name="dest"/> (target filesystem) only when
        /// the target is missing or its last-write-time differs from the source's; otherwise it's a no-op
        /// (so redundant events and full reconciles don't re-transfer unchanged files).
        /// </summary>
        private async Task CopyIfChangedAsync(Ctx ctx, string source, string dest, string targetDir, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            bool exists;
            try
            {
                exists = ctx.TargetFs.FileExists(dest);
                if (exists && ctx.SourceFs.GetLastWriteTimeUtc(source) == ctx.TargetFs.GetLastWriteTimeUtc(dest))
                {
                    return; // unchanged — nothing to do
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to read timestamps for '{dest}'", ex);
                return;
            }

            if (await CopyThroughTempAsync(ctx, source, dest, targetDir, log, result, ct))
            {
                if (exists)
                {
                    result.Updated++;
                    await log.AppendAsync($"Updated '{dest}' (source changed)");
                }
                else
                {
                    result.Copied++;
                    await log.AppendAsync($"Copied '{source}' -> '{dest}'");
                }
            }
        }

        /// <summary>
        /// Crash-safe copy across (possibly different) filesystems: streams the local source into a
        /// dot-prefixed temp on the target filesystem, stamps it with the source's last-write-time, then (on
        /// success) removes any existing destination and renames the temp onto it. On any failure the temp is
        /// removed so a partial/temp file is never left behind. (Idiom duplicated from
        /// <see cref="FolderPairSynchronizer"/>, per project convention.) Returns success.
        /// </summary>
        private async Task<bool> CopyThroughTempAsync(Ctx ctx, string source, string dest, string targetDir, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var tempPath = Path.Combine(targetDir, "." + Path.GetFileName(dest) + ".tmp");
            try
            {
                var sourceTime = ctx.SourceFs.GetLastWriteTimeUtc(source);

                using (var input = ctx.SourceFs.OpenRead(source))
                using (var output = ctx.TargetFs.OpenWrite(tempPath))
                {
                    await input.CopyToAsync(output, ct);
                }

                // Carry the source's timestamp across so the next run sees the target as up to date.
                ctx.TargetFs.SetLastWriteTimeUtc(tempPath, sourceTime);

                if (ctx.TargetFs.FileExists(dest))
                {
                    ctx.TargetFs.DeleteFile(dest);
                }
                ctx.TargetFs.MoveFile(tempPath, dest, overwrite: false);
                result.BytesCopied += TrySize(ctx, source);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Stopped mid-copy — drop the partial temp and let cancellation unwind.
                TryDeleteTemp(ctx, tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(ctx, tempPath);
                if (FileLock.IsSkippableReadError(ex, out var reason))
                {
                    // The file couldn't be read (locked, or an unavailable cloud file) — skip it as a
                    // non-fatal warning rather than failing the run.
                    result.Warnings++;
                    await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{source}' — {reason}");
                }
                else
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to copy '{source}' -> '{dest}'", ex);
                }
                return false;
            }
        }

        /// <summary>Walks the local Lightroom tree once, indexing raw files by basename (case-insensitive).</summary>
        private Dictionary<string, List<string>> BuildRawIndex(LightroomArchiveSettings settings)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(settings.LightroomFolder) ||
                settings.RawExtensions.Count == 0 ||
                !localFileSystem.DirectoryExists(settings.LightroomFolder))
            {
                return index;
            }

            var stack = new Stack<string>();
            stack.Push(settings.LightroomFolder);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                try
                {
                    foreach (var file in localFileSystem.GetFiles(dir))
                    {
                        if (!settings.RawExtensions.Contains(Path.GetExtension(file)))
                        {
                            continue;
                        }
                        var key = Path.GetFileNameWithoutExtension(file);
                        if (!index.TryGetValue(key, out var list))
                        {
                            index[key] = list = [];
                        }
                        list.Add(file);
                    }
                }
                catch
                {
                    // An unreadable folder simply contributes no raws.
                    continue;
                }

                try
                {
                    foreach (var sub in localFileSystem.GetDirectories(dir))
                    {
                        stack.Push(sub);
                    }
                }
                catch
                {
                    // Can't enumerate sub-folders — skip them.
                }
            }

            return index;
        }

        private static long TrySize(Ctx ctx, string path)
        {
            try
            {
                return ctx.SourceFs.GetFileSize(path);
            }
            catch
            {
                return 0;
            }
        }

        private static void TryDeleteTemp(Ctx ctx, string tempPath)
        {
            try
            {
                if (ctx.TargetFs.FileExists(tempPath))
                {
                    ctx.TargetFs.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        /// <summary>The resolved filesystems and settings for one batch.</summary>
        private sealed record Ctx(
            IBackupFileSystem SourceFs,
            IBackupFileSystem TargetFs,
            LightroomArchiveItem Item,
            string TargetBase,
            LightroomArchiveSettings Settings,
            IReadOnlyDictionary<string, List<string>> RawIndex);
    }
}
