using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="ILightroomArchiveProcessor"/>. Endpoint-aware (modelled on
    /// <see cref="OneWaySyncSynchronizer"/>'s cross-filesystem copy rather than the local-only
    /// <see cref="InstantSyncProcessor"/>): the source and Lightroom catalog are always local, while the
    /// target is resolved through <see cref="IEndpointFileSystemFactory"/> so a copy streams from the local
    /// filesystem into a crash-safe temp on the target filesystem (local or a remote connection). Each copied
    /// file's matching raw sidecar(s) are pulled from the Lightroom folder into a RAW sub-folder beside it.
    /// </summary>
    public sealed class LightroomArchiveProcessor(IEndpointFileSystemFactory endpointFactory, IBackupFileSystem localFileSystem) : ILightroomArchiveProcessor
    {
        public async Task<BackupResult> ProcessBatchAsync(
            LightroomArchiveItem item,
            int? targetConnectionId,
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

            var target = await endpointFactory.ResolveAsync(targetConnectionId, item.TargetFolder, cancellationToken);
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
                // A folder in the batch is one that arrived — created, moved in, or renamed (the watcher drops a
                // folder's own "changed" events). Windows reports that as one event for the folder and nothing for
                // what's inside it, so archive its whole contents; otherwise a renamed folder would reach the target
                // empty while the delete of its old name removed the archived copy. A non-recursive item leaves
                // sub-folders alone entirely.
                if (ctx.SourceFs.DirectoryExists(sourcePath))
                {
                    if (ctx.Item.IncludeSubFolders && await EnsureDirectoryAsync(ctx, destPath, log, result))
                    {
                        await ArchiveArrivedFolderAsync(ctx, sourcePath, log, result, ct);
                    }
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

        private async Task ArchiveArrivedFolderAsync(Ctx ctx, string sourceDir, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            IReadOnlyList<string> files, subDirs;
            try
            {
                files = ctx.SourceFs.GetFiles(sourceDir);
                subDirs = ctx.SourceFs.GetDirectories(sourceDir);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to list '{sourceDir}'", ex);
                return;
            }

            foreach (var path in files.Concat(subDirs))
            {
                ct.ThrowIfCancellationRequested();
                await ApplyChangeAsync(ctx, path, log, result, ct); // a sub-folder recurses through the folder branch
            }
        }

        /// <summary>Copies the matching raw sidecar(s) for <paramref name="sourcePath"/> into the RAW sub-folder of its target directory.</summary>
        private async Task CopyMatchingRawsAsync(Ctx ctx, string sourcePath, string targetDir, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(baseName) || !ctx.RawIndex.TryGetValue(baseName, out var rawPaths))
            {
                return;
            }

            // Camera counters wrap, so a catalog can hold several raws with the same name in different folders. They
            // would overwrite each other in the RAW folder (leaving another shoot's raw, re-copied every run), so when
            // a name isn't unique among the matches each copy is named after its catalog folder instead.
            var sharedNames = rawPaths
                .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                var rawName = sharedNames.Contains(Path.GetFileName(rawPath))
                    ? DisambiguatedRawName(rawPath, ctx.Settings.LightroomFolder)
                    : Path.GetFileName(rawPath);
                var rawDest = Path.Combine(rawDir, rawName);
                await CopyIfChangedAsync(ctx, rawPath, rawDest, rawDir, log, result, ct);
            }
        }

        // "lr\2024\05\IMG_0001.ARW" -> "IMG_0001 (2024_05).ARW": stable across runs, and unique, since two raws can't
        // share both a name and a folder.
        internal static string DisambiguatedRawName(string rawPath, string? lightroomFolder)
        {
            var directory = Path.GetDirectoryName(rawPath) ?? string.Empty;
            var relative = string.IsNullOrEmpty(lightroomFolder) ? directory : Path.GetRelativePath(lightroomFolder, directory);
            var tag = relative.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_').Replace(':', '_');
            return $"{Path.GetFileNameWithoutExtension(rawPath)} ({tag}){Path.GetExtension(rawPath)}";
        }

        // Whether a file in the RAW folder is a raw for photos with this base name — plain, or folder-tagged.
        private static bool IsRawFor(string rawFileName, string baseName)
        {
            var stem = Path.GetFileNameWithoutExtension(rawFileName);
            return string.Equals(stem, baseName, StringComparison.OrdinalIgnoreCase)
                || (stem.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase) && stem.EndsWith(')'));
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

                    // Mirror the raw sidecar(s) for this file from the RAW folder beside it — unless another source
                    // file with the same base name (IMG_1.jpg beside a deleted IMG_1.tif) still needs them.
                    if (!SiblingSharesBaseName(ctx, sourcePath))
                    {
                        await DeleteMatchingRawsAsync(ctx, sourcePath, Path.GetDirectoryName(destPath)!, log, result);
                    }
                }
                else if (ctx.Item.IncludeSubFolders && ctx.TargetFs.DirectoryExists(destPath))
                {
                    // (A non-recursive item doesn't manage target sub-folders, so it never deletes one.)
                    ctx.TargetFs.DeleteDirectory(destPath, recursive: true);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted folder '{destPath}' (removed from source)");
                }
                // Nothing at the target — nothing to mirror, nothing logged.
            }
            catch (ProtectedFileException ex)
            {
                // The target protects it (a Google Docs file on Drive, or a folder holding one) — keep it.
                result.Warnings++;
                await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{destPath}' — {ex.Reason}");
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete '{destPath}'", ex);
            }
        }

        // True when another file in the deleted file's source folder has the same base name — its raws are shared.
        // Unreadable → assume so: keeping a raw is safe, deleting one another photo needs is not.
        private static bool SiblingSharesBaseName(Ctx ctx, string deletedSourcePath)
        {
            var baseName = Path.GetFileNameWithoutExtension(deletedSourcePath);
            try
            {
                return ctx.SourceFs.GetFiles(Path.GetDirectoryName(deletedSourcePath)!)
                    .Any(f => string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return true;
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
                    !IsRawFor(Path.GetFileName(rawFile), baseName))
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
                if (exists && IsUnchanged(ctx.SourceFs.GetFileStat(source), ctx.TargetFs.GetFileStat(dest)))
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

        // FAT/exFAT targets (common for photo drives) store write times to 2-second granularity, rounding up, so a
        // copy reads back up to 2 s newer. An exact compare saw every archived file as changed and re-copied it on
        // every pass; within the tolerance and the same size, it's unchanged (as in the One Way Sync engine).
        private static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

        private static bool IsUnchanged(FileStat source, FileStat dest) =>
            source.Size == dest.Size && (source.LastWriteTimeUtc - dest.LastWriteTimeUtc).Duration() <= WriteTimeTolerance;

        /// <summary>
        /// Crash-safe copy across (possibly different) filesystems: streams the local source into a
        /// dot-prefixed temp on the target filesystem, stamps it with the source's last-write-time, then (on
        /// success) removes any existing destination and renames the temp onto it. On any failure the temp is
        /// removed so a partial/temp file is never left behind. (Idiom duplicated from
        /// <see cref="OneWaySyncSynchronizer"/>, per project convention.) Returns success.
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
                    try
                    {
                        await input.CopyToAsync(output, ct);
                    }
                    catch
                    {
                        AbandonableWrite.Abandon(output); // a partial copy isn't uploaded on the way out
                        throw;
                    }
                }

                // Carry the source's timestamp across so the next run sees the target as up to date.
                ctx.TargetFs.SetLastWriteTimeUtc(tempPath, sourceTime);

                // One overwrite-rename: deleting the old copy first lost both if the rename then failed.
                ctx.TargetFs.MoveFile(tempPath, dest, overwrite: true);
                result.BytesCopied += TrySize(ctx, source);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Stopped mid-copy — drop the partial temp and let cancellation unwind.
                TryDeleteTemp(ctx, tempPath);
                throw;
            }
            catch (ProtectedFileException ex)
            {
                // The target refused to replace the destination (a Google Docs file on Drive) — keep it.
                TryDeleteTemp(ctx, tempPath);
                result.Warnings++;
                await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{dest}' — {ex.Reason}");
                return false;
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
