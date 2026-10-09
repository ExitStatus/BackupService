using System.Buffers;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IOneWaySyncSynchronizer"/>. Walks the source tree one folder at a time and
    /// mirrors it into the target per the pair's rules (see <c>CLAUDE.md</c> / the handler). The source
    /// and target may live on different filesystems (local or a remote SMB connection): each side is
    /// resolved to an <see cref="IBackupFileSystem"/> via <see cref="IEndpointFileSystemFactory"/>, and a
    /// copy streams from the source filesystem into a crash-safe temp on the target filesystem before
    /// renaming. All filesystem access goes through the abstraction so the decision logic is testable.
    /// </summary>
    public sealed class OneWaySyncSynchronizer(IEndpointFileSystemFactory endpointFactory) : IOneWaySyncSynchronizer
    {
        public async Task<BackupResult> SyncAsync(OneWaySyncItem pair, int? sourceConnectionId, int? targetConnectionId, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null, Action<string?>? onCurrentFile = null)
        {
            var result = new BackupResult();
            // Include/exclude rules filter which files are synced (empty includes = all files).
            var filter = new BackupFilter(pair.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));

            var source = await endpointFactory.ResolveAsync(sourceConnectionId, pair.SourceFolder, cancellationToken);
            try
            {
                var target = await endpointFactory.ResolveAsync(targetConnectionId, pair.TargetFolder, cancellationToken);
                try
                {
                    var ctx = new SyncContext(source.FileSystem, target.FileSystem, pair, filter, onCurrentFile);
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

        public async Task<int> CountFilesAsync(OneWaySyncItem pair, int? sourceConnectionId, CancellationToken cancellationToken)
        {
            var filter = new BackupFilter(pair.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            var source = await endpointFactory.ResolveAsync(sourceConnectionId, pair.SourceFolder, cancellationToken);
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
        private static int CountDirectory(IBackupFileSystem fs, string dir, IReadOnlyList<string> ancestors, OneWaySyncItem pair, BackupFilter filter, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            int count;
            try
            {
                count = fs.GetFiles(dir).Count(p => filter.IsFileInScope(PathHelper.GetLeafName(p), ancestors));
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
                    var name = PathHelper.GetLeafName(sub);
                    if (filter.ExcludesFolder(name) || filter.ExcludesPath([.. ancestors, name]) || DirectoryLinkLoop.Target(fs, sub) is not null)
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
            var sourceFileNames = new HashSet<string>(sourceFiles.Select(PathHelper.GetLeafName), StringComparer.OrdinalIgnoreCase);
            if (targetFiles.Any(p => IsCrashSafeTempName(PathHelper.GetLeafName(p)) && !sourceFileNames.Contains(PathHelper.GetLeafName(p))))
            {
                var kept = new List<string>(targetFiles.Count);
                foreach (var targetPath in targetFiles)
                {
                    var name = PathHelper.GetLeafName(targetPath);
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

            var targetNames = new HashSet<string>(targetFiles.Select(PathHelper.GetLeafName), StringComparer.OrdinalIgnoreCase);

            // Only files in scope per the include/exclude rules are synced (empty includes = all files).
            var inScopeSourceFiles = sourceFiles
                .Where(p => filter.IsFileInScope(PathHelper.GetLeafName(p), ancestors))
                .ToList();

            // 4. Copy/update each in-scope source file. Each one reports a single unit of progress when done
            // (via the finally), so the count matches CountFilesAsync's denominator regardless of the outcome.
            foreach (var sourcePath in inScopeSourceFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = PathHelper.GetLeafName(sourcePath);
                    var destPath = Path.Combine(targetDir, name);

                    if (!targetNames.Contains(name))
                    {
                        if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, null, ctx, log, result, ct))
                        {
                            result.Copied++;
                            await log.AppendAsync($"Copied '{sourcePath}' -> '{destPath}'");
                        }
                        continue;
                    }

                    // One metadata lookup per side gives both the write time (the copy/skip decision) and the size
                    // (the truncated-copy check below), so an unchanged file costs no more than a timestamp compare.
                    FileStat sourceStat, destStat;
                    try
                    {
                        sourceStat = ctx.SourceFs.GetFileStat(sourcePath);
                        destStat = ctx.TargetFs.GetFileStat(destPath);
                    }
                    catch (EndpointUnavailableException)
                    {
                        throw; // source endpoint gone — abort the run (handled by the handler)
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // One side changed between the folder listing and now.
                        if (!StillExists(ctx.SourceFs, sourcePath))
                        {
                            continue; // the source file was deleted mid-run — nothing to back up (the next run mirrors it)
                        }
                        if (!StillExists(ctx.TargetFs, destPath)
                            && await CopyThroughTempAsync(sourcePath, destPath, targetDir, null, ctx, log, result, ct))
                        {
                            result.Copied++; // the destination vanished — copy it afresh
                            await log.AppendAsync($"Copied '{sourcePath}' -> '{destPath}'");
                            continue;
                        }
                        if (StillExists(ctx.TargetFs, destPath))
                        {
                            result.Errors++;
                            await log.ErrorAsync($"Failed to read timestamps for '{destPath}'", ex);
                        }
                        continue;
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        await log.ErrorAsync($"Failed to read timestamps for '{destPath}'", ex);
                        continue;
                    }

                    var sourceTime = sourceStat.LastWriteTimeUtc;
                    var destTime = destStat.LastWriteTimeUtc;

                    if (WriteTimesEqual(sourceTime, destTime))
                    {
                        // Same timestamp (within filesystem granularity) — normally no change, nothing logged. But a
                        // destination SHORTER than its source can't be a faithful copy (e.g. an earlier transfer that
                        // ended early yet was stamped with the source's time), so repair it rather than skip forever.
                        if (IsTruncatedCopy(sourceStat, destStat))
                        {
                            if (!MayReplaceDestination(pair.OverwriteBehaviour, sourceTime, destTime))
                            {
                                // It reads as marginally newer (FAT rounding, or a genuine edit) and the pair doesn't
                                // overwrite newer files — say so, rather than leave a possibly-truncated copy silently.
                                result.Warnings++;
                                await log.AppendAsync(OperationLogLevel.Warning,
                                    $"Kept '{destPath}' — it's {destStat.Size} bytes but the source is {sourceStat.Size} bytes with the same timestamp, so it may be an incomplete copy; it wasn't replaced because it reads as slightly newer and this pair doesn't overwrite newer files");
                            }
                            else if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, sourceStat, ctx, log, result, ct))
                            {
                                // A warning as well as an update: the copy is now right, but an earlier backup wasn't.
                                result.Updated++;
                                result.Warnings++;
                                await log.AppendAsync(OperationLogLevel.Warning,
                                    $"Repaired '{destPath}' — it was {destStat.Size} bytes but the source is {sourceStat.Size} bytes with the same timestamp, so an earlier copy was incomplete");
                            }
                        }
                    }
                    else if (sourceTime > destTime)
                    {
                        if (await CopyThroughTempAsync(sourcePath, destPath, targetDir, sourceStat, ctx, log, result, ct))
                        {
                            result.Updated++;
                            await log.AppendAsync($"Updated '{destPath}' (source is newer)");
                        }
                    }
                    else
                    {
                        // Destination is meaningfully newer — the overwrite behaviour decides.
                        await ApplyOverwriteBehaviourAsync(pair.OverwriteBehaviour, sourcePath, destPath, sourceStat, targetDir, ctx, log, result, ct);
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
                var sourceNames = new HashSet<string>(inScopeSourceFiles.Select(PathHelper.GetLeafName), StringComparer.OrdinalIgnoreCase);
                foreach (var targetPath in targetFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var targetName = PathHelper.GetLeafName(targetPath);
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
                    catch (ProtectedFileException ex)
                    {
                        await LogKeptAsync(targetPath, ex, log, result);
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
                catch (EndpointUnavailableException)
                {
                    throw; // source endpoint gone — abort the run
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to enumerate sub-folders of '{sourceDir}'", ex);
                    return;
                }

                foreach (var sourceSub in sourceDirs)
                {
                    var name = PathHelper.GetLeafName(sourceSub);
                    // An excluded folder's (by name, or by exact relative path) whole subtree is left out.
                    if (filter.ExcludesFolder(name) || filter.ExcludesPath([.. ancestors, name]))
                    {
                        continue;
                    }
                    if (DirectoryLinkLoop.Target(ctx.SourceFs, sourceSub) is { } loopTarget)
                    {
                        await log.AppendAsync($"Skipped '{sourceSub}' — it's a link back to '{loopTarget}', which this backup is already inside");
                        continue;
                    }
                    await SyncDirectoryAsync(sourceSub, Path.Combine(targetDir, name), [.. ancestors, name], ctx, log, result, fileProgress, ct);
                }

                if (pair.AllowDeletions)
                {
                    var sourceSubNames = new HashSet<string>(sourceDirs.Select(PathHelper.GetLeafName), StringComparer.OrdinalIgnoreCase);
                    foreach (var targetSub in targetDirs)
                    {
                        ct.ThrowIfCancellationRequested();
                        var targetSubName = PathHelper.GetLeafName(targetSub);
                        // Don't delete an excluded target subtree (it's out of scope, not an orphan).
                        if (!sourceSubNames.Contains(targetSubName) &&
                            !filter.ExcludesFolder(targetSubName) &&
                            !filter.ExcludesPath([.. ancestors, targetSubName]))
                        {
                            await DeleteOrphanDirectoryAsync(targetSub, [.. ancestors, targetSubName], ctx, log, result, ct);
                        }
                    }
                }
            }
        }

        private async Task ApplyOverwriteBehaviourAsync(
            OverwriteBehaviour behaviour, string source, string dest, FileStat sourceStat, string targetDir, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            switch (behaviour)
            {
                case OverwriteBehaviour.AlwaysOverwrite:
                    if (await CopyThroughTempAsync(source, dest, targetDir, sourceStat, ctx, log, result, ct))
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
                            TryStampWriteTime(ctx.TargetFs, dest, sourceStat.LastWriteTimeUtc);
                            result.Updated++;
                            await log.AppendAsync($"Synced timestamp of '{dest}' (content matched)");
                        }
                        // Content differs — leave the newer destination untouched (no change, no log).
                    }
                    catch (EndpointUnavailableException)
                    {
                        throw; // source endpoint gone — abort the run
                    }
                    catch (Exception ex) when (FileLock.IsSkippableReadError(ex, out var reason))
                    {
                        // Couldn't read a side to compare (locked, an unavailable cloud file, or a Google Docs
                        // file that can't be downloaded) — the same non-fatal warning as a skipped copy.
                        result.Warnings++;
                        await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{source}' — {reason}");
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
        /// <paramref name="knownSourceStat"/> is the source's metadata when the caller has already read it
        /// (otherwise it's read here).
        /// </summary>
        private async Task<bool> CopyThroughTempAsync(string source, string dest, string targetDir, FileStat? knownSourceStat, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var tempPath = Path.Combine(targetDir, CrashSafeTempName(Path.GetFileName(dest)!));
            // Surface the file about to be copied to the View Progress dialog (best-effort, UI only).
            ctx.OnCurrentFile?.Invoke(Path.GetFileName(dest));
            try
            {
                var sourceStat = knownSourceStat ?? ctx.SourceFs.GetFileStat(source);

                long written;
                using (var input = ctx.SourceFs.OpenRead(source, ct))
                using (var output = ctx.TargetFs.OpenWrite(tempPath))
                {
                    // Pass the token so a Stop mid-copy interrupts a large file promptly (the catch below
                    // removes the partial temp, so nothing is left behind). Counting the bytes as they stream
                    // saves a metadata round trip on the target to find out how much was written.
                    try
                    {
                        written = await CopyCountingAsync(input, output, ct);
                    }
                    catch
                    {
                        AbandonableWrite.Abandon(output); // a partial copy isn't uploaded on the way out
                        throw;
                    }
                }

                // Never commit a short copy: a source stream that ended early (e.g. a flaky transfer) would
                // otherwise be renamed into place and stamped with the source's time, so later runs would see it
                // as unchanged. A file that grew mid-copy (a live log) is larger, not smaller, so only short counts.
                if (sourceStat.Size > 0 && written < sourceStat.Size)
                {
                    if (SizeChanged(ctx.SourceFs, source, sourceStat.Size))
                    {
                        // The source itself shrank while it was being read (a save or truncation mid-copy), so what
                        // was read may mix old and new content. Skip it as a warning, like a locked file.
                        TryDeleteTemp(ctx, tempPath);
                        result.Warnings++;
                        await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{source}' — it changed while it was being copied (it will be retried on the next run)");
                        return false;
                    }

                    throw new IOException($"Incomplete copy: only {written} of {sourceStat.Size} bytes were transferred.");
                }

                // The sync engine compares LastWriteTimeUtc to decide copy/skip, so carry the source's
                // timestamp across (a fresh-write "now" timestamp would look newer on the next run).
                TryStampWriteTime(ctx.TargetFs, tempPath, sourceStat.LastWriteTimeUtc);

                // One overwrite-rename: deleting the old copy first lost both if the rename then failed.
                ctx.TargetFs.MoveFile(tempPath, dest, overwrite: true);
                result.BytesCopied += written;
                return true;
            }
            catch (OperationCanceledException)
            {
                // Stopped mid-copy — drop the partial temp and let cancellation unwind the run. Not
                // counted as an error or warning (it isn't a problem with the file).
                TryDeleteTemp(ctx, tempPath);
                throw;
            }
            catch (EndpointUnavailableException)
            {
                // The source endpoint went away (e.g. the camera was switched off). Clean up and let it
                // propagate so the whole run aborts fast rather than failing every remaining file.
                TryDeleteTemp(ctx, tempPath);
                throw;
            }
            catch (ProtectedFileException ex)
            {
                // The target refused to replace the destination (a Google Docs file on Drive) — keep it.
                TryDeleteTemp(ctx, tempPath);
                await LogKeptAsync(dest, ex, log, result);
                return false;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(ctx, tempPath);
                if (ex is FileNotFoundException or DirectoryNotFoundException && !StillExists(ctx.SourceFs, source))
                {
                    return false; // the source was deleted after the folder was listed — nothing to back up
                }
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

        // Win32 FileTime (and thus File.SetLastWriteTimeUtc) only accepts times from 1601-01-01 UTC onward.
        // A source that can't supply a usable timestamp — e.g. an MTP camera that exposes no modified date —
        // yields DateTime.MinValue, which would otherwise throw "Not a valid Win32 FileTime." and fail the copy.
        private static readonly DateTime MinFileTimeUtc = DateTime.FromFileTimeUtc(0);

        // FAT/exFAT (common on USB drives) store modification times at 2-second granularity, rounding up, so a copied
        // file reads back a slightly *newer* timestamp than its source. Treat write-times within this window as equal
        // (like robocopy /FFT) so an unchanged file isn't re-copied every run because of the filesystem's rounding.
        private static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

        private static bool WriteTimesEqual(DateTime a, DateTime b) => (a - b).Duration() <= WriteTimeTolerance;

        // Carry the source's last-write-time onto a freshly copied file, but only when it's a stampable
        // Win32 FileTime; if the source had no usable date we leave the new file's natural timestamp.
        private static void TryStampWriteTime(IBackupFileSystem fs, string path, DateTime sourceTime)
        {
            if (sourceTime >= MinFileTimeUtc)
            {
                fs.SetLastWriteTimeUtc(path, sourceTime);
            }
        }

        private static bool ContentEqual(SyncContext ctx, string source, string dest)
        {
            using var a = ctx.SourceFs.OpenRead(source);
            using var b = ctx.TargetFs.OpenRead(dest);
            return StreamCompare.Equal(a, b);
        }

        private const int CopyBufferSize = 81920; // Stream.CopyToAsync's default

        // Streams input to output, returning the number of bytes copied.
        private static async Task<long> CopyCountingAsync(Stream input, Stream output, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
                return total;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // A destination is only treated as a truncated copy when it's SHORTER than a source of known size. Never
        // when it's larger (that isn't truncation — e.g. a source rewritten smaller with its time preserved), and
        // never against a source with no usable size (≤ 0 — an MTP device reporting 0, or a sentinel that overflows
        // to -1, for a real file): either would re-copy the same file on every run.
        private static bool IsTruncatedCopy(FileStat source, FileStat dest) => source.Size > 0 && dest.Size < source.Size;

        // Within the write-time tolerance the destination can still read as marginally NEWER than the source — FAT
        // rounding, or a genuine edit made moments after the source. Replacing a newer destination is the overwrite
        // behaviour's call, exactly as when it's newer outright: only AlwaysOverwrite allows it (the sizes differ, so
        // UpdateOnlyIfContentMatches can never match).
        private static bool MayReplaceDestination(OverwriteBehaviour behaviour, DateTime sourceTime, DateTime destTime) =>
            destTime <= sourceTime || behaviour == OverwriteBehaviour.AlwaysOverwrite;

        // Whether a file is (still) there. An unreadable answer counts as "yes", so a real failure gets reported rather
        // than silently skipped; a gone endpoint still aborts the run.
        private static bool StillExists(IBackupFileSystem fs, string path)
        {
            try
            {
                return fs.FileExists(path);
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch
            {
                return true;
            }
        }

        // Re-reads the source's size after a short copy: true only when it can be read and is no longer the size the
        // copy started from. A gone endpoint still aborts the run.
        private static bool SizeChanged(IBackupFileSystem fs, string path, long sizeAtStart)
        {
            try
            {
                return fs.GetFileSize(path) != sizeAtStart;
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch
            {
                return false;
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

        // Deletes an orphan target folder and everything in it that's in scope. Returns whether the folder is now gone.
        // relative = the folder's path below the target root (ancestors + its name), for the include/exclude rules.
        private async Task<bool> DeleteOrphanDirectoryAsync(string directory, IReadOnlyList<string> relative, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<string> files, subDirs;
            try
            {
                // A junction or directory symlink points somewhere else entirely: remove just the link, never walk
                // into it — deleting "its" files would delete the real ones outside the target.
                if (ctx.TargetFs.IsDirectoryLink(directory))
                {
                    ctx.TargetFs.DeleteDirectory(directory, recursive: false);
                    await log.AppendAsync($"Deleted folder link '{directory}' (not in source; what it points to was left alone)");
                    return true;
                }

                files = ctx.TargetFs.GetFiles(directory);
                subDirs = ctx.TargetFs.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access folder '{directory}'", ex);
                return false;
            }

            // Whether everything inside was removed. Anything kept — out of scope per the include/exclude rules
            // (exactly as it would be if the folder still existed in the source), protected, or not deletable — means
            // the folder stays: a folder delete would fail on a non-empty folder locally, and on Google Drive it
            // would silently take whatever was left with it.
            var emptied = true;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!ctx.Filter.IsFileInScope(PathHelper.GetLeafName(file), relative))
                {
                    emptied = false;
                    continue;
                }

                try
                {
                    ctx.TargetFs.DeleteFile(file);
                    result.Deleted++;
                    await log.AppendAsync($"Deleted '{file}' (not in source)");
                }
                catch (ProtectedFileException ex)
                {
                    emptied = false;
                    await LogKeptAsync(file, ex, log, result);
                }
                catch (Exception ex)
                {
                    emptied = false;
                    result.Errors++;
                    await log.ErrorAsync($"Failed to delete '{file}'", ex);
                }
            }

            foreach (var sub in subDirs)
            {
                var name = PathHelper.GetLeafName(sub);
                if (ctx.Filter.ExcludesFolder(name) || ctx.Filter.ExcludesPath([.. relative, name])
                    || !await DeleteOrphanDirectoryAsync(sub, [.. relative, name], ctx, log, result, ct))
                {
                    emptied = false; // an excluded sub-folder is out of scope, not an orphan — kept
                }
            }

            if (!emptied)
            {
                return false;
            }

            try
            {
                ctx.TargetFs.DeleteDirectory(directory, recursive: false);
                await log.AppendAsync($"Deleted folder '{directory}' (not in source)");
                return true;
            }
            catch (ProtectedFileException ex)
            {
                await LogKeptAsync(directory, ex, log, result);
                return false;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete folder '{directory}'", ex);
                return false;
            }
        }

        // The target refused to delete or overwrite a file it protects (a Google Docs file on Drive): it's kept, and
        // that's a warning, not an error — the backup did what it should.
        private static async Task LogKeptAsync(string path, ProtectedFileException ex, IOperationLogger log, BackupResult result)
        {
            result.Warnings++;
            await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{path}' — {ex.Reason}");
        }

        /// <summary>The resolved filesystems and rules for one sync run.</summary>
        private sealed record SyncContext(IBackupFileSystem SourceFs, IBackupFileSystem TargetFs, OneWaySyncItem Pair, BackupFilter Filter, Action<string?>? OnCurrentFile);
    }
}
