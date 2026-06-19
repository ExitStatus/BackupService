using System.Globalization;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IArchiveSyncProcessor"/>. Builds the ZIP in a local temp folder, then copies
    /// it into the target crash-safe (via a dot-prefixed temp then rename — the same idiom as
    /// <see cref="FolderPairSynchronizer"/>). Retention is derived from the target folder listing: each
    /// archive's level (for GFS) is encoded in its file name, so no extra state is needed beyond the
    /// item's run counter. All filesystem access goes through <see cref="IBackupFileSystem"/> so the
    /// retention/promotion logic is unit-testable.
    /// </summary>
    public sealed class ArchiveSyncProcessor(IBackupFileSystem fileSystem) : IArchiveSyncProcessor
    {
        private const string TimestampFormat = "yyyy-MM-dd_HHmmss";

        public async Task<BackupResult> CreateArchiveAsync(
            ArchiveSyncItem item, long runIndex, DateTime timestamp, IOperationLogger log, CancellationToken cancellationToken)
        {
            var result = new BackupResult();

            // 1. The source must exist before we try to archive it.
            try
            {
                if (!fileSystem.DirectoryExists(item.SourceFolder))
                {
                    result.Errors++;
                    await log.ErrorAsync($"Source folder '{item.SourceFolder}' does not exist.");
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access source folder '{item.SourceFolder}'", ex);
                return result;
            }

            var gfs = item.RetentionMode == ArchiveRetentionMode.GrandfatherFatherSon;
            var stamp = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            // GFS archives are born at level 1 (the "son"); Keep-last-N has no level token.
            var finalName = gfs ? $"{item.FileName}_L1_{stamp}.zip" : $"{item.FileName}_{stamp}.zip";

            // 2. Build the ZIP in a local temp folder, verbose-logging each file added. Include/exclude
            // rules filter which source files go into the archive (empty includes = all files).
            var filter = new BackupFilter(item.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            string tempZip;
            ZipBuildResult build;
            try
            {
                tempZip = fileSystem.GetTempFilePath(finalName);
                build = fileSystem.CreateZipFromDirectory(
                    item.SourceFolder, tempZip, item.IncludeSubFolders,
                    filter.IsEmpty ? null : filter.IsRelativePathInScope);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to create archive of '{item.SourceFolder}'", ex);
                return result;
            }

            if (build.Added.Count > 0)
            {
                // Verbose, but as a SINGLE Debug detail row (the file list won't change mid-zip), with one
                // file per CRLF-separated line — rather than thousands of rows. Counts as Info for the header.
                var lines = new List<string>(build.Added.Count + 1) { $"Archived {build.Added.Count} file(s):" };
                lines.AddRange(build.Added);
                await log.AppendAsync(OperationLogLevel.Debug, string.Join("\r\n", lines));
            }

            // Files that couldn't be read (locked/in use) are skipped, not fatal — log each as a Warning
            // and count it so the run summary reports "completed with N error(s)" while still archiving the
            // rest.
            foreach (var skip in build.Skipped)
            {
                result.Errors++;
                await log.AppendAsync(OperationLogLevel.Warning,
                    $"Skipped file '{skip.EntryName}' (in use or unreadable): {skip.Reason}");
            }

            // 3. Crash-safe copy into the target folder.
            if (!await EnsureDirectoryAsync(item.TargetFolder, log, result))
            {
                TryDelete(tempZip);
                return result;
            }

            var destPath = Path.Combine(item.TargetFolder, finalName);
            var copied = await CopyThroughTempAsync(tempZip, destPath, item.TargetFolder, log, result);
            TryDelete(tempZip); // best-effort cleanup of the local temp build, copied or not

            if (!copied)
            {
                return result;
            }

            result.Copied++;
            await log.AppendAsync($"Created archive '{destPath}'");

            // 4. Apply retention. A failure here is logged but doesn't undo the archive just made.
            try
            {
                if (gfs)
                {
                    await ApplyGfsRetentionAsync(item, runIndex, log, result, cancellationToken);
                }
                else
                {
                    await ApplyKeepLastNAsync(item, log, result, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to apply retention in '{item.TargetFolder}'", ex);
            }

            return result;
        }

        // ---- Retention ----

        private async Task ApplyKeepLastNAsync(ArchiveSyncItem item, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var keep = Math.Max(1, item.RetentionCount);
            var archives = ListArchives(item, gfs: false)
                .OrderByDescending(a => a.Timestamp)
                .ToList();

            foreach (var old in archives.Skip(keep))
            {
                ct.ThrowIfCancellationRequested();
                await DeleteArchiveAsync(old, log, result);
            }
        }

        private async Task ApplyGfsRetentionAsync(ArchiveSyncItem item, long runIndex, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var n = Math.Max(1, item.RetentionCount);
            var maxLevels = Math.Max(1, item.MaxLevels);

            // Promote oldest-rolls-up, lowest level first, on cadence (every n^level runs). Re-listing
            // per level means an archive promoted into the next level can itself promote on in the same
            // run when that level's cadence also lands.
            for (var level = 1; level < maxLevels; level++)
            {
                if (!IsPromotionDue(runIndex, n, level))
                {
                    continue;
                }

                var oldest = ListArchives(item, gfs: true)
                    .Where(a => a.Level == level)
                    .OrderBy(a => a.Timestamp)
                    .FirstOrDefault();
                if (oldest is not null)
                {
                    await PromoteAsync(item, oldest, level + 1, log, result, ct);
                }
            }

            // Trim every level to N (the aged-out archives that weren't promoted; top-level overflow too).
            for (var level = 1; level <= maxLevels; level++)
            {
                ct.ThrowIfCancellationRequested();
                var atLevel = ListArchives(item, gfs: true)
                    .Where(a => a.Level == level)
                    .OrderByDescending(a => a.Timestamp)
                    .ToList();
                foreach (var old in atLevel.Skip(n))
                {
                    await DeleteArchiveAsync(old, log, result);
                }
            }
        }

        /// <summary>A level promotes its oldest up every <c>n^level</c> runs.</summary>
        private static bool IsPromotionDue(long runIndex, int n, int level)
        {
            long period = 1;
            for (var i = 0; i < level; i++)
            {
                period *= n;
                // Once the period exceeds the run index it can't divide it (runIndex >= 1), so stop.
                if (period > runIndex)
                {
                    return false;
                }
            }
            return runIndex % period == 0;
        }

        private async Task PromoteAsync(ArchiveSyncItem item, ArchiveFile archive, int newLevel, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var stamp = archive.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var newPath = Path.Combine(item.TargetFolder, $"{item.FileName}_L{newLevel}_{stamp}.zip");
            try
            {
                fileSystem.MoveFile(archive.Path, newPath, overwrite: true);
                await log.AppendAsync($"Promoted archive '{archive.Path}' to level {newLevel}");
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to promote archive '{archive.Path}' to level {newLevel}", ex);
            }
        }

        private async Task DeleteArchiveAsync(ArchiveFile archive, IOperationLogger log, BackupResult result)
        {
            try
            {
                fileSystem.DeleteFile(archive.Path);
                result.Deleted++;
                await log.AppendAsync($"Pruned archive '{archive.Path}'");
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to prune archive '{archive.Path}'", ex);
            }
        }

        /// <summary>
        /// Lists the target archives belonging to <paramref name="item"/> (by file-name prefix and the
        /// expected level/timestamp shape). Files whose timestamp token doesn't parse are skipped so a
        /// foreign file is never treated as one of ours.
        /// </summary>
        private IReadOnlyList<ArchiveFile> ListArchives(ArchiveSyncItem item, bool gfs)
        {
            IReadOnlyList<string> files;
            try
            {
                files = fileSystem.GetFiles(item.TargetFolder);
            }
            catch
            {
                return [];
            }

            var prefix = item.FileName + "_";
            var archives = new List<ArchiveFile>();
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var core = name[prefix.Length..^4]; // strip the "{FileName}_" prefix and ".zip"
                var level = 1;
                var token = core;

                if (gfs)
                {
                    // Expect "L{level}_{timestamp}".
                    if (core.Length < 2 || (core[0] != 'L' && core[0] != 'l'))
                    {
                        continue;
                    }
                    var underscore = core.IndexOf('_');
                    if (underscore < 2 || !int.TryParse(core[1..underscore], out level))
                    {
                        continue;
                    }
                    token = core[(underscore + 1)..];
                }

                if (!DateTime.TryParseExact(token, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                {
                    continue;
                }

                archives.Add(new ArchiveFile(path, level, ts));
            }

            return archives;
        }

        // ---- Crash-safe copy (the same idiom as FolderPairSynchronizer / InstantSyncProcessor) ----

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
                TryDelete(tempPath);
                result.Errors++;
                await log.ErrorAsync($"Failed to copy '{source}' -> '{dest}'", ex);
                return false;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (fileSystem.FileExists(path))
                {
                    fileSystem.DeleteFile(path);
                }
            }
            catch
            {
                // Best-effort cleanup — never let a secondary failure mask the original error.
            }
        }

        private sealed record ArchiveFile(string Path, int Level, DateTime Timestamp);
    }
}
