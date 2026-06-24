using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IFolderPairSynchronizer"/>. Walks the source tree one folder at a time and
    /// mirrors it into the target per the pair's rules (see <c>CLAUDE.md</c> / the handler). The source
    /// and target may live on different filesystems (local or a remote SMB connection): each side is
    /// resolved to an <see cref="IBackupFileSystem"/> via <see cref="IEndpointFileSystemFactory"/>, and a
    /// copy streams from the source filesystem into a crash-safe temp on the target filesystem before
    /// renaming. All filesystem access goes through the abstraction so the decision logic is testable.
    /// </summary>
    public sealed class FolderPairSynchronizer(IEndpointFileSystemFactory endpointFactory) : IFolderPairSynchronizer
    {
        public async Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null)
        {
            var result = new BackupResult();
            // Include/exclude rules filter which files are synced (empty includes = all files).
            var filter = new BackupFilter(pair.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));

            var source = await endpointFactory.ResolveAsync(pair.SourceConnectionId, pair.SourceFolder, cancellationToken);
            try
            {
                var target = await endpointFactory.ResolveAsync(pair.TargetConnectionId, pair.TargetFolder, cancellationToken);
                try
                {
                    var ctx = new SyncContext(source.FileSystem, target.FileSystem, pair, filter);
                    await SyncDirectoryAsync(source.BasePath, target.BasePath, [], ctx, log, result, fileProgress, cancellationToken);
                }
                finally
                {
                    target.Session.Dispose();
                }
            }
            finally
            {
                source.Session.Dispose();
            }

            return result;
        }

        public async Task<int> CountFilesAsync(FolderPair pair, CancellationToken cancellationToken)
        {
            var filter = new BackupFilter(pair.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            var source = await endpointFactory.ResolveAsync(pair.SourceConnectionId, pair.SourceFolder, cancellationToken);
            try
            {
                return CountDirectory(source.FileSystem, source.BasePath, [], pair, filter, cancellationToken);
            }
            finally
            {
                source.Session.Dispose();
            }
        }

        // Source-only walk mirroring SyncDirectoryAsync's scoping: counts in-scope files, recursing the same
        // sub-folders the sync would. An unreadable folder contributes nothing (the sync will log that error).
        private static int CountDirectory(IBackupFileSystem fs, string dir, IReadOnlyList<string> ancestors, FolderPair pair, BackupFilter filter, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            int count;
            try
            {
                count = fs.GetFiles(dir).Count(p => filter.IsFileInScope(Path.GetFileName(p)!, ancestors));
            }
            catch
            {
                return 0;
            }

            if (pair.IncludeSubFolders)
            {
                IReadOnlyList<string> dirs;
                try
                {
                    dirs = fs.GetDirectories(dir);
                }
                catch
                {
                    return count;
                }

                foreach (var sub in dirs)
                {
                    var name = Path.GetFileName(sub)!;
                    if (filter.ExcludesFolder(name) || filter.ExcludesPath([.. ancestors, name]))
                    {
                        continue;
                    }
                    count += CountDirectory(fs, sub, [.. ancestors, name], pair, filter, ct);
                }
            }

            return count;
        }

        private async Task SyncDirectoryAsync(
            string sourceDir, string targetDir, IReadOnlyList<string> ancestors, SyncContext ctx, IOperationLogger log, BackupResult result, IProgress<int>? fileProgress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var pair = ctx.Pair;
            var filter = ctx.Filter;

            // 1. List the source files (can't proceed with this subtree without them).
            IReadOnlyList<string> sourceFiles;
            try
            {
                sourceFiles = ctx.SourceFs.GetFiles(sourceDir);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access source folder '{sourceDir}'", ex);
                return;
            }

            // 2. Ensure the target folder exists.
            try
            {
                if (!ctx.TargetFs.DirectoryExists(targetDir))
                {
                    ctx.TargetFs.CreateDirectory(targetDir);
                    await log.AppendAsync($"Created folder '{targetDir}'");
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to create target folder '{targetDir}'", ex);
                return;
            }

            // 3. List the target files (for existence checks and deletions).
            IReadOnlyList<string> targetFiles;
            try
            {
                targetFiles = ctx.TargetFs.GetFiles(targetDir);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access target folder '{targetDir}'", ex);
                return;
            }

            // 3a. Sweep leftover crash-safe temp files from a previously interrupted run (e.g. the machine
            // hibernated mid-copy). A ".{name}.tmp" that isn't itself a source file is never real backup
            // content, so remove it regardless of AllowDeletions and exclude it from the rest of this folder.
            var sourceFileNames = new HashSet<string>(sourceFiles.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
            if (targetFiles.Any(p => IsCrashSafeTempName(Path.GetFileName(p)!) && !sourceFileNames.Contains(Path.GetFileName(p)!)))
            {
                var kept = new List<string>(targetFiles.Count);
                foreach (var targetPath in targetFiles)
                {
                    var name = Path.GetFileName(targetPath)!;
                    if (!IsCrashSafeTempName(name) || sourceFileNames.Contains(name))
                    {
                        kept.Add(targetPath);
                        continue;
                    }

                    try
                    {
                        ctx.TargetFs.DeleteFile(targetPath);
                        await log.AppendAsync($"Removed leftover temp file '{targetPath}'");
                    }
                    catch (Exception ex)
                    {
                        // Best-effort cleanup — never fail the run over a stray temp.
                        await log.AppendAsync(OperationLogLevel.Warning, $"Could not remove leftover temp file '{targetPath}': {ex.Message}");
                        kept.Add(targetPath);
                    }
                }
                targetFiles = kept;
            }

            var targetNames = new HashSet<string>(targetFiles.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);

            // Only files in scope per the include/exclude rules are synced (empty includes = all files).
            var inScopeSourceFiles = sourceFiles
                .Where(p => filter.IsFileInScope(Path.GetFileName(p)!, ancestors))
                .ToList();

            // 4. Copy/update each in-scope source file. Each one reports a single unit of progress when done
            // (via the finally), so the count matches CountFilesAsync's denominator regardless of the outcome.
            foreach (var sourcePath in inScopeSourceFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = Path.GetFileName(sourcePath)!;
                    var destPath = Path.Combine(targetDir, name);

                    if (!targetNames.Contains(name))
                    {
                        if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, ctx, log, result))
                        {
                            result.Copied++;
                            await log.AppendAsync($"Copied '{sourcePath}' -> '{destPath}'");
                        }
                        continue;
                    }

                    DateTime sourceTime, destTime;
                    try
                    {
                        sourceTime = ctx.SourceFs.GetLastWriteTimeUtc(sourcePath);
                        destTime = ctx.TargetFs.GetLastWriteTimeUtc(destPath);
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        await log.ErrorAsync($"Failed to read timestamps for '{destPath}'", ex);
                        continue;
                    }

                    if (sourceTime > destTime)
                    {
                        if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, ctx, log, result))
                        {
                            result.Updated++;
                            await log.AppendAsync($"Updated '{destPath}' (source is newer)");
                        }
                    }
                    else if (sourceTime == destTime)
                    {
                        // Same timestamp — no change, nothing logged.
                    }
                    else
                    {
                        // Destination is newer — the overwrite behaviour decides.
                        await ApplyOverwriteBehaviourAsync(pair.OverwriteBehaviour, sourcePath, destPath, sourceTime, targetDir, ctx, log, result);
                    }
                }
                finally
                {
                    fileProgress?.Report(1);
                }
            }

            // 5. Delete orphan target files (mirror). Only mirror in-scope files: a target file that is
            // out of scope (e.g. matches an exclude rule, or isn't in the include list) is left untouched.
            if (pair.AllowDeletions)
            {
                var sourceNames = new HashSet<string>(inScopeSourceFiles.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
                foreach (var targetPath in targetFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetName = Path.GetFileName(targetPath)!;
                    if (sourceNames.Contains(targetName) || !filter.IsFileInScope(targetName, ancestors))
                    {
                        continue;
                    }

                    try
                    {
                        ctx.TargetFs.DeleteFile(targetPath);
                        result.Deleted++;
                        await log.AppendAsync($"Deleted '{targetPath}' (not in source)");
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        await log.ErrorAsync($"Failed to delete '{targetPath}'", ex);
                    }
                }
            }

            // 6. Recurse into sub-folders, one at a time.
            if (pair.IncludeSubFolders)
            {
                IReadOnlyList<string> sourceDirs, targetDirs;
                try
                {
                    sourceDirs = ctx.SourceFs.GetDirectories(sourceDir);
                    targetDirs = pair.AllowDeletions ? ctx.TargetFs.GetDirectories(targetDir) : [];
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to enumerate sub-folders of '{sourceDir}'", ex);
                    return;
                }

                foreach (var sourceSub in sourceDirs)
                {
                    var name = Path.GetFileName(sourceSub)!;
                    // An excluded folder's (by name, or by exact relative path) whole subtree is left out.
                    if (filter.ExcludesFolder(name) || filter.ExcludesPath([.. ancestors, name]))
                    {
                        continue;
                    }
                    await SyncDirectoryAsync(sourceSub, Path.Combine(targetDir, name), [.. ancestors, name], ctx, log, result, fileProgress, ct);
                }

                if (pair.AllowDeletions)
                {
                    var sourceSubNames = new HashSet<string>(sourceDirs.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
                    foreach (var targetSub in targetDirs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var targetSubName = Path.GetFileName(targetSub)!;
                        // Don't delete an excluded target subtree (it's out of scope, not an orphan).
                        if (!sourceSubNames.Contains(targetSubName) &&
                            !filter.ExcludesFolder(targetSubName) &&
                            !filter.ExcludesPath([.. ancestors, targetSubName]))
                        {
                            await DeleteOrphanDirectoryAsync(targetSub, ctx, log, result, ct);
                        }
                    }
                }
            }
        }

        private async Task ApplyOverwriteBehaviourAsync(
            OverwriteBehaviour behaviour, string source, string dest, DateTime sourceTime, string targetDir, SyncContext ctx, IOperationLogger log, BackupResult result)
        {
            switch (behaviour)
            {
                case OverwriteBehaviour.AlwaysOverwrite:
                    if (await CopyThroughTempAsync(source, dest, targetDir, ctx, log, result))
                    {
                        result.Updated++;
                        await log.AppendAsync($"Overwrote '{dest}' (destination was newer, always-overwrite)");
                    }
                    break;

                case OverwriteBehaviour.UpdateOnlyIfContentMatches:
                    try
                    {
                        if (ContentEqual(ctx, source, dest))
                        {
                            ctx.TargetFs.SetLastWriteTimeUtc(dest, sourceTime);
                            result.Updated++;
                            await log.AppendAsync($"Synced timestamp of '{dest}' (content matched)");
                        }
                        // Content differs — leave the newer destination untouched (no change, no log).
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        await log.ErrorAsync($"Failed to compare/update '{dest}'", ex);
                    }
                    break;

                case OverwriteBehaviour.DoNotOverwriteNewer:
                default:
                    // Destination is newer and must be preserved — skip (no change, no log).
                    break;
            }
        }

        /// <summary>
        /// Crash-safe copy across (possibly different) filesystems: streams the source into a dot-prefixed
        /// temp on the target filesystem, stamps it with the source's last-write-time, then (on success)
        /// removes any existing destination and renames the temp onto it. On any failure the temp is removed
        /// so a partial/temp file is never left behind; the error is logged. Returns success.
        /// </summary>
        private async Task<bool> CopyThroughTempAsync(string source, string dest, string targetDir, SyncContext ctx, IOperationLogger log, BackupResult result)
        {
            var tempPath = Path.Combine(targetDir, CrashSafeTempName(Path.GetFileName(dest)!));
            try
            {
                var sourceTime = ctx.SourceFs.GetLastWriteTimeUtc(source);

                using (var input = ctx.SourceFs.OpenRead(source))
                using (var output = ctx.TargetFs.OpenWrite(tempPath))
                {
                    await input.CopyToAsync(output);
                }

                // The sync engine compares LastWriteTimeUtc to decide copy/skip, so carry the source's
                // timestamp across (a fresh-write "now" timestamp would look newer on the next run).
                ctx.TargetFs.SetLastWriteTimeUtc(tempPath, sourceTime);

                if (ctx.TargetFs.FileExists(dest))
                {
                    ctx.TargetFs.DeleteFile(dest);
                }
                ctx.TargetFs.MoveFile(tempPath, dest, overwrite: false);
                result.BytesCopied += TrySize(ctx, source);
                return true;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(ctx, tempPath);
                if (FileLock.IsSkippableReadError(ex, out var reason))
                {
                    // The file couldn't be read (locked, or an unavailable cloud file) — skip it this run
                    // as a non-fatal warning rather than failing the run.
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

        private static bool ContentEqual(SyncContext ctx, string source, string dest)
        {
            using var a = ctx.SourceFs.OpenRead(source);
            using var b = ctx.TargetFs.OpenRead(dest);
            return StreamCompare.Equal(a, b);
        }

        // Best-effort source file size for the bytes-copied stat — never let a stats read fail the copy.
        private static long TrySize(SyncContext ctx, string path)
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

        // The crash-safe copy writes each file to a deterministic dot-prefixed temp before renaming it into
        // place ("report.pdf" -> ".report.pdf.tmp"). Centralised so the writer and the leftover-sweep agree.
        private static string CrashSafeTempName(string fileName) => $".{fileName}.tmp";

        private static bool IsCrashSafeTempName(string fileName) =>
            fileName.Length > 5 && fileName[0] == '.' && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        private static void TryDeleteTemp(SyncContext ctx, string tempPath)
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
                // Best-effort cleanup — never let a secondary failure mask the original error.
            }
        }

        private async Task DeleteOrphanDirectoryAsync(string directory, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<string> files, subDirs;
            try
            {
                files = ctx.TargetFs.GetFiles(directory);
                subDirs = ctx.TargetFs.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access folder '{directory}'", ex);
                return;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ctx.TargetFs.DeleteFile(file);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted '{file}' (not in source)");
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to delete '{file}'", ex);
                }
            }

            foreach (var sub in subDirs)
            {
                await DeleteOrphanDirectoryAsync(sub, ctx, log, result, ct);
            }

            try
            {
                ctx.TargetFs.DeleteDirectory(directory, recursive: false);
                await log.AppendAsync($"Deleted folder '{directory}' (not in source)");
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete folder '{directory}'", ex);
            }
        }

        /// <summary>The resolved filesystems and rules for one sync run.</summary>
        private sealed record SyncContext(IBackupFileSystem SourceFs, IBackupFileSystem TargetFs, FolderPair Pair, BackupFilter Filter);
    }
}
