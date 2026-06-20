using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IFolderPairSynchronizer"/>. Walks the source tree one folder at a time and
    /// mirrors it into the target per the pair's rules (see <c>CLAUDE.md</c> / the handler). All
    /// filesystem access goes through <see cref="IBackupFileSystem"/> so the decision logic is
    /// unit-testable; copies are crash-safe (written to a dot-prefixed temp, then renamed).
    /// </summary>
    public sealed class FolderPairSynchronizer(IBackupFileSystem fileSystem) : IFolderPairSynchronizer
    {
        public async Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken)
        {
            var result = new BackupResult();
            // Include/exclude rules filter which files are synced (empty includes = all files).
            var filter = new BackupFilter(pair.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            await SyncDirectoryAsync(pair.SourceFolder, pair.TargetFolder, [], pair, filter, log, result, cancellationToken);
            return result;
        }

        private async Task SyncDirectoryAsync(
            string sourceDir, string targetDir, IReadOnlyList<string> ancestors, FolderPair pair, BackupFilter filter, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // 1. List the source files (can't proceed with this subtree without them).
            IReadOnlyList<string> sourceFiles;
            try
            {
                sourceFiles = fileSystem.GetFiles(sourceDir);
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
                if (!fileSystem.DirectoryExists(targetDir))
                {
                    fileSystem.CreateDirectory(targetDir);
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
                targetFiles = fileSystem.GetFiles(targetDir);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access target folder '{targetDir}'", ex);
                return;
            }

            var targetNames = new HashSet<string>(targetFiles.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);

            // Only files in scope per the include/exclude rules are synced (empty includes = all files).
            var inScopeSourceFiles = sourceFiles
                .Where(p => filter.IsFileInScope(Path.GetFileName(p)!, ancestors))
                .ToList();

            // 4. Copy/update each in-scope source file.
            foreach (var sourcePath in inScopeSourceFiles)
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(sourcePath)!;
                var destPath = Path.Combine(targetDir, name);

                if (!targetNames.Contains(name))
                {
                    if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, log, result))
                    {
                        result.Copied++;
                        await log.AppendAsync($"Copied '{sourcePath}' -> '{destPath}'");
                    }
                    continue;
                }

                DateTime sourceTime, destTime;
                try
                {
                    sourceTime = fileSystem.GetLastWriteTimeUtc(sourcePath);
                    destTime = fileSystem.GetLastWriteTimeUtc(destPath);
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to read timestamps for '{destPath}'", ex);
                    continue;
                }

                if (sourceTime > destTime)
                {
                    if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, log, result))
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
                    await ApplyOverwriteBehaviourAsync(pair.OverwriteBehaviour, sourcePath, destPath, sourceTime, targetDir, log, result);
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
                        fileSystem.DeleteFile(targetPath);
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
                    sourceDirs = fileSystem.GetDirectories(sourceDir);
                    targetDirs = pair.AllowDeletions ? fileSystem.GetDirectories(targetDir) : [];
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
                    await SyncDirectoryAsync(sourceSub, Path.Combine(targetDir, name), [.. ancestors, name], pair, filter, log, result, ct);
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
                            await DeleteOrphanDirectoryAsync(targetSub, log, result, ct);
                        }
                    }
                }
            }
        }

        private async Task ApplyOverwriteBehaviourAsync(
            OverwriteBehaviour behaviour, string source, string dest, DateTime sourceTime, string targetDir, IOperationLogger log, BackupResult result)
        {
            switch (behaviour)
            {
                case OverwriteBehaviour.AlwaysOverwrite:
                    if (await CopyThroughTempAsync(source, dest, targetDir, log, result))
                    {
                        result.Updated++;
                        await log.AppendAsync($"Overwrote '{dest}' (destination was newer, always-overwrite)");
                    }
                    break;

                case OverwriteBehaviour.UpdateOnlyIfContentMatches:
                    try
                    {
                        if (fileSystem.FilesContentEqual(source, dest))
                        {
                            fileSystem.SetLastWriteTimeUtc(dest, sourceTime);
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
                return true;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(tempPath);
                result.Errors++;
                await log.ErrorAsync($"Failed to copy '{source}' -> '{dest}'", ex);
                return false;
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

        private async Task DeleteOrphanDirectoryAsync(string directory, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<string> files, subDirs;
            try
            {
                files = fileSystem.GetFiles(directory);
                subDirs = fileSystem.GetDirectories(directory);
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
                    fileSystem.DeleteFile(file);
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
                await DeleteOrphanDirectoryAsync(sub, log, result, ct);
            }

            try
            {
                fileSystem.DeleteDirectory(directory, recursive: false);
                await log.AppendAsync($"Deleted folder '{directory}' (not in source)");
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete folder '{directory}'", ex);
            }
        }
    }
}
