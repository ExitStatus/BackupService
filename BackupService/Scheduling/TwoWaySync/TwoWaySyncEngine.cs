using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;

namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>
    /// Default <see cref="ITwoWaySyncEngine"/>. Walks the union of both trees one folder at a time and reconciles
    /// each file against the persisted baseline:
    /// <list type="bullet">
    /// <item>only one side changed → apply that change to the other (copy an add/modify; on a delete, delete the
    /// other side when <see cref="TwoWaySyncItem.PropagateDeletions"/>, else restore from the surviving side);</item>
    /// <item>both changed → resolve per <see cref="TwoWaySyncItem.ConflictResolution"/>.</item>
    /// </list>
    /// A fresh baseline is built as it goes and saved only after a completed pass, so a cancelled/aborted run
    /// leaves the previous baseline intact. Copies are crash-safe (dot-temp → stamp mtime → rename) and stream
    /// across the two filesystems in either direction.
    /// </summary>
    public sealed class TwoWaySyncEngine(IEndpointFileSystemFactory endpointFactory, ITwoWaySyncStateStore stateStore) : ITwoWaySyncEngine
    {
        // FAT/exFAT (USB) store 2-second-granularity times; treat write-times within this window as equal (like
        // robocopy /FFT) so a round-tripped file isn't seen as "modified" on the next run.
        private static readonly TimeSpan WriteTimeTolerance = TimeSpan.FromSeconds(2);

        // Win32 FileTime floor — a source with no usable date (e.g. an MTP camera) yields DateTime.MinValue,
        // which SetLastWriteTimeUtc rejects; skip stamping in that case.
        private static readonly DateTime MinFileTimeUtc = DateTime.FromFileTimeUtc(0);

        public async Task<BackupResult> SyncAsync(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null, Action<string?>? onCurrentFile = null)
        {
            var result = new BackupResult();
            var filter = new BackupFilter(item.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            var baseline = await stateStore.LoadAsync(item.Id, cancellationToken);
            var newBaseline = new Dictionary<string, TwoWaySyncEntry>(StringComparer.OrdinalIgnoreCase);

            var left = await endpointFactory.ResolveAsync(sourceConnectionId, item.SourceFolder, cancellationToken);
            try
            {
                var right = await endpointFactory.ResolveAsync(targetConnectionId, item.TargetFolder, cancellationToken);
                try
                {
                    var ctx = new SyncContext(left.FileSystem, right.FileSystem, item, filter, baseline, newBaseline, onCurrentFile);
                    await SyncDirectoryAsync(left.BasePath, right.BasePath, [], ctx, log, result, fileProgress, cancellationToken);
                }
                finally
                {
                    right.Session.Dispose();
                }
            }
            finally
            {
                left.Session.Dispose();
            }

            // Persist the updated baseline only after a completed pass (cancellation/abort throws before here,
            // leaving the previous baseline so nothing is mis-classified next run).
            await stateStore.SaveAsync(item.Id, newBaseline, cancellationToken);
            return result;
        }

        public async Task<int> CountFilesAsync(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId, CancellationToken cancellationToken)
        {
            var filter = new BackupFilter(item.Filters.Select(f => new FilterRule(f.Direction, f.Kind, f.Pattern)));
            var left = await endpointFactory.ResolveAsync(sourceConnectionId, item.SourceFolder, cancellationToken);
            try
            {
                var right = await endpointFactory.ResolveAsync(targetConnectionId, item.TargetFolder, cancellationToken);
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    CountDirectory(left.FileSystem, left.BasePath, right.FileSystem, right.BasePath, [], item, filter, seen, cancellationToken);
                    return seen.Count;
                }
                finally
                {
                    right.Session.Dispose();
                }
            }
            finally
            {
                left.Session.Dispose();
            }
        }

        private static void CountDirectory(IBackupFileSystem leftFs, string leftDir, IBackupFileSystem rightFs, string rightDir, IReadOnlyList<string> ancestors, TwoWaySyncItem item, BackupFilter filter, HashSet<string> seen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var name in InScopeNamesSafe(leftFs, leftDir, filter, ancestors).Concat(InScopeNamesSafe(rightFs, rightDir, filter, ancestors)))
            {
                seen.Add(MakeKey(ancestors, name));
            }

            if (!item.IncludeSubFolders)
            {
                return;
            }

            var subs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in ListDirsSafe(leftFs, leftDir)) subs.Add(Path.GetFileName(d)!);
            foreach (var d in ListDirsSafe(rightFs, rightDir)) subs.Add(Path.GetFileName(d)!);

            foreach (var sub in subs)
            {
                if (filter.ExcludesFolder(sub) || filter.ExcludesPath([.. ancestors, sub]))
                {
                    continue;
                }
                CountDirectory(leftFs, Path.Combine(leftDir, sub), rightFs, Path.Combine(rightDir, sub), [.. ancestors, sub], item, filter, seen, ct);
            }
        }

        private async Task SyncDirectoryAsync(string leftDir, string rightDir, IReadOnlyList<string> ancestors, SyncContext ctx, IOperationLogger log, BackupResult result, IProgress<int>? fileProgress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var filter = ctx.Filter;

            var leftFiles = await ListFilesAsync(ctx.LeftFs, leftDir, log, result);
            var rightFiles = await ListFilesAsync(ctx.RightFs, rightDir, log, result);
            if (leftFiles is null || rightFiles is null)
            {
                return; // a side couldn't be listed — skip this subtree (already logged)
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            names.UnionWith(InScopeNames(leftFiles, ctx.LeftFs, filter, ancestors));
            names.UnionWith(InScopeNames(rightFiles, ctx.RightFs, filter, ancestors));

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ReconcileFileAsync(name, ancestors, leftDir, rightDir, ctx, log, result, ct);
                }
                finally
                {
                    fileProgress?.Report(1);
                }
            }

            if (!ctx.Item.IncludeSubFolders)
            {
                return;
            }

            var subs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in ListDirsSafe(ctx.LeftFs, leftDir)) subs.Add(Path.GetFileName(d)!);
            foreach (var d in ListDirsSafe(ctx.RightFs, rightDir)) subs.Add(Path.GetFileName(d)!);

            foreach (var sub in subs)
            {
                if (filter.ExcludesFolder(sub) || filter.ExcludesPath([.. ancestors, sub]))
                {
                    continue;
                }
                await SyncDirectoryAsync(Path.Combine(leftDir, sub), Path.Combine(rightDir, sub), [.. ancestors, sub], ctx, log, result, fileProgress, ct);
            }
        }

        private async Task ReconcileFileAsync(string name, IReadOnlyList<string> ancestors, string leftDir, string rightDir, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var key = MakeKey(ancestors, name);

            FileState left, right;
            try
            {
                left = ReadState(ctx.LeftFs, Path.Combine(leftDir, name));
                right = ReadState(ctx.RightFs, Path.Combine(rightDir, name));
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to read state of '{key}'", ex);
                return;
            }

            var hasBase = ctx.Baseline.TryGetValue(key, out var baseEntry);
            var changedL = HasChanged(left, hasBase, baseEntry);
            var changedR = HasChanged(right, hasBase, baseEntry);

            if (!changedL && !changedR)
            {
                // Both consistent with the baseline. Keep the entry while the file is present on both sides.
                if (left.Exists && right.Exists)
                {
                    ctx.NewBaseline[key] = BaselineOf(left);
                }
                return;
            }

            if (changedL != changedR)
            {
                // Exactly one side changed — it is the authority; apply its change to the other side.
                var (authFs, authDir, auth, folFs, folDir, fol) = changedL
                    ? (ctx.LeftFs, leftDir, left, ctx.RightFs, rightDir, right)
                    : (ctx.RightFs, rightDir, right, ctx.LeftFs, leftDir, left);
                await ApplyOneSidedAsync(auth, fol, authFs, authDir, folFs, folDir, name, key, changedL, ctx, log, result, ct);
                return;
            }

            // Both changed — a conflict (or a coincidental match / both-deleted).
            await ResolveConflictAsync(left, right, name, ancestors, leftDir, rightDir, key, hasBase, baseEntry, ctx, log, result, ct);
        }

        // Exactly one side changed. Copy an add/modify to the other; propagate or restore a deletion.
        private async Task ApplyOneSidedAsync(FileState authority, FileState follower, IBackupFileSystem authFs, string authDir, IBackupFileSystem folFs, string folDir, string name, string key, bool authorityIsLeft, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var direction = authorityIsLeft ? "source→target" : "target→source";

            if (authority.Exists)
            {
                var authPath = Path.Combine(authDir, name);
                if (await CopyAndBaselineAsync(follower.Exists, key, authFs, authPath, authority, folFs, folDir, name, ctx, log, result, ct))
                {
                    await log.AppendAsync($"{(follower.Exists ? "Updated" : "Copied")} '{key}' ({direction})");
                }
                return;
            }

            // Authority deleted the file since the baseline.
            if (ctx.Item.PropagateDeletions)
            {
                if (follower.Exists)
                {
                    if (await TryDeleteAsync(folFs, Path.Combine(folDir, name), key, log, result))
                    {
                        result.Deleted++;
                        await log.AppendAsync($"Deleted '{key}' ({direction}, deletion propagated)");
                    }
                }
                // Not added to the new baseline — the file is gone from both sides.
            }
            else if (follower.Exists)
            {
                // Deletions aren't propagated — restore the file from the surviving side.
                var folPath = Path.Combine(folDir, name);
                if (await CopyAndBaselineAsync(false, key, folFs, folPath, follower, authFs, authDir, name, ctx, log, result, ct))
                {
                    await log.AppendAsync($"Restored '{key}' ({(authorityIsLeft ? "target→source" : "source→target")}, deletion not propagated)");
                }
            }
        }

        private async Task ResolveConflictAsync(FileState left, FileState right, string name, IReadOnlyList<string> ancestors, string leftDir, string rightDir, string key, bool hasBase, TwoWaySyncEntry baseEntry, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            // Both changed but ended identical, or both deleted — no real conflict.
            if (!left.Exists && !right.Exists)
            {
                return; // both deleted → drop from baseline
            }
            if (left.Exists && right.Exists && SameState(left, right))
            {
                ctx.NewBaseline[key] = BaselineOf(left);
                return;
            }

            switch (ctx.Item.ConflictResolution)
            {
                case ConflictResolution.NewerWins:
                    // A present (modified) file beats a deletion; when both are present, the newer wins.
                    if (left.Exists && (!right.Exists || left.Mtime >= right.Mtime))
                    {
                        await CopyConflictWinnerAsync(left, right.Exists, ctx.LeftFs, leftDir, ctx.RightFs, rightDir, name, key, "source", ctx, log, result, ct);
                    }
                    else
                    {
                        await CopyConflictWinnerAsync(right, left.Exists, ctx.RightFs, rightDir, ctx.LeftFs, leftDir, name, key, "target", ctx, log, result, ct);
                    }
                    break;

                case ConflictResolution.SourceWins:
                    await ApplyWinnerSideAsync(left, right, leftDir, rightDir, name, key, sourceWins: true, ctx, log, result, ct);
                    break;

                case ConflictResolution.TargetWins:
                    await ApplyWinnerSideAsync(left, right, leftDir, rightDir, name, key, sourceWins: false, ctx, log, result, ct);
                    break;

                case ConflictResolution.KeepBoth:
                    if (left.Exists && right.Exists)
                    {
                        await KeepBothAsync(left, right, name, ancestors, leftDir, rightDir, key, ctx, log, result, ct);
                    }
                    else
                    {
                        // Edit-vs-delete: keep the edited (present) copy; there's no second version to preserve.
                        if (left.Exists)
                        {
                            await CopyConflictWinnerAsync(left, false, ctx.LeftFs, leftDir, ctx.RightFs, rightDir, name, key, "source (kept edit over delete)", ctx, log, result, ct);
                        }
                        else
                        {
                            await CopyConflictWinnerAsync(right, false, ctx.RightFs, rightDir, ctx.LeftFs, leftDir, name, key, "target (kept edit over delete)", ctx, log, result, ct);
                        }
                    }
                    break;

                case ConflictResolution.Skip:
                default:
                    result.Warnings++;
                    await log.AppendAsync(OperationLogLevel.Warning, $"Conflict on '{key}' — both sides changed; skipped (left untouched).");
                    // Preserve the old baseline entry so the conflict keeps being flagged until resolved.
                    if (hasBase)
                    {
                        ctx.NewBaseline[key] = baseEntry;
                    }
                    break;
            }
        }

        // The winning side (source or target) is authoritative: its state — including a deletion — is applied to the other.
        private async Task ApplyWinnerSideAsync(FileState left, FileState right, string leftDir, string rightDir, string name, string key, bool sourceWins, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var (winner, winnerFs, winnerDir, winnerLabel, loser, loserFs, loserDir, loserLabel) = sourceWins
                ? (left, ctx.LeftFs, leftDir, "source", right, ctx.RightFs, rightDir, "target")
                : (right, ctx.RightFs, rightDir, "target", left, ctx.LeftFs, leftDir, "source");

            if (winner.Exists)
            {
                var winnerPath = Path.Combine(winnerDir, name);
                if (await CopyAndBaselineAsync(loser.Exists, key, winnerFs, winnerPath, winner, loserFs, loserDir, name, ctx, log, result, ct))
                {
                    await log.AppendAsync($"Conflict on '{key}' — {winnerLabel} wins, copied {winnerLabel}→{loserLabel}");
                }
            }
            else if (loser.Exists)
            {
                // Winner deleted the file → its deletion wins.
                if (await TryDeleteAsync(loserFs, Path.Combine(loserDir, name), key, log, result))
                {
                    result.Deleted++;
                    await log.AppendAsync($"Conflict on '{key}' — {winnerLabel} wins, deleted on {loserLabel}");
                }
            }
        }

        // Copies a conflict winner (present file) over the other side and logs it as a conflict resolution.
        private async Task CopyConflictWinnerAsync(FileState winner, bool loserExists, IBackupFileSystem winnerFs, string winnerDir, IBackupFileSystem loserFs, string loserDir, string name, string key, string label, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var winnerPath = Path.Combine(winnerDir, name);
            if (await CopyAndBaselineAsync(loserExists, key, winnerFs, winnerPath, winner, loserFs, loserDir, name, ctx, log, result, ct))
            {
                await log.AppendAsync($"Conflict on '{key}' — {label} won.");
            }
        }

        // Keep both edited versions: the source copy stays as the canonical name; the target's differing copy is
        // renamed with a " (conflict …)" suffix, and both files end up on both sides.
        private async Task KeepBothAsync(FileState left, FileState right, string name, IReadOnlyList<string> ancestors, string leftDir, string rightDir, string key, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            var conflictName = BuildConflictName(name);
            var conflictKey = MakeKey(ancestors, conflictName);
            var leftPath = Path.Combine(leftDir, name);
            var rightPath = Path.Combine(rightDir, name);
            var rightConflictPath = Path.Combine(rightDir, conflictName);

            try
            {
                // Set the target's copy aside so the source copy can take the canonical name.
                ctx.RightFs.MoveFile(rightPath, rightConflictPath, overwrite: false);
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Conflict on '{key}' — failed to set aside the target copy", ex);
                return;
            }

            var okCanonical = await CopyAndBaselineAsync(true, key, ctx.LeftFs, leftPath, left, ctx.RightFs, rightDir, name, ctx, log, result, ct);
            var okConflict = await CopyAndBaselineAsync(false, conflictKey, ctx.RightFs, rightConflictPath, right, ctx.LeftFs, leftDir, conflictName, ctx, log, result, ct);

            if (okCanonical || okConflict)
            {
                await log.AppendAsync(OperationLogLevel.Warning, $"Conflict on '{key}' — kept both (source kept as '{name}', target saved as '{conflictName}').");
            }
        }

        // Crash-safe copy from one filesystem to another + baseline update on success. destExisted picks
        // Copied vs Updated. Returns whether the copy succeeded (a skipped/failed copy leaves no baseline entry
        // so it retries next run).
        private async Task<bool> CopyAndBaselineAsync(bool destExisted, string key, IBackupFileSystem fromFs, string fromPath, FileState from, IBackupFileSystem toFs, string toDir, string toName, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            if (!await CopyThroughTempAsync(fromFs, fromPath, toFs, toDir, toName, from.Mtime, ctx.OnCurrentFile, log, result, ct))
            {
                return false;
            }

            if (destExisted)
            {
                result.Updated++;
            }
            else
            {
                result.Copied++;
            }
            ctx.NewBaseline[key] = new TwoWaySyncEntry(from.Mtime.Ticks, from.Size);
            return true;
        }

        private async Task<bool> CopyThroughTempAsync(IBackupFileSystem fromFs, string fromPath, IBackupFileSystem toFs, string toDir, string toName, DateTime fromMtime, Action<string?>? onCurrentFile, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            try
            {
                if (!toFs.DirectoryExists(toDir))
                {
                    toFs.CreateDirectory(toDir);
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to create folder '{toDir}'", ex);
                return false;
            }

            var destPath = Path.Combine(toDir, toName);
            var tempPath = Path.Combine(toDir, CrashSafeTempName(toName));
            onCurrentFile?.Invoke(toName);
            try
            {
                using (var input = fromFs.OpenRead(fromPath))
                using (var output = toFs.OpenWrite(tempPath))
                {
                    await input.CopyToAsync(output, ct);
                }

                TryStampWriteTime(toFs, tempPath, fromMtime);

                if (toFs.FileExists(destPath))
                {
                    toFs.DeleteFile(destPath);
                }
                toFs.MoveFile(tempPath, destPath, overwrite: false);
                result.BytesCopied += TrySize(fromFs, fromPath);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDeleteTemp(toFs, tempPath);
                throw;
            }
            catch (EndpointUnavailableException)
            {
                TryDeleteTemp(toFs, tempPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteTemp(toFs, tempPath);
                if (FileLock.IsSkippableReadError(ex, out var reason))
                {
                    result.Warnings++;
                    await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{fromPath}' — {reason}");
                }
                else
                {
                    result.Errors++;
                    await log.ErrorAsync($"Failed to copy '{fromPath}' -> '{destPath}'", ex);
                }
                return false;
            }
        }

        private static async Task<bool> TryDeleteAsync(IBackupFileSystem fs, string path, string key, IOperationLogger log, BackupResult result)
        {
            try
            {
                fs.DeleteFile(path);
                return true;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to delete '{key}'", ex);
                return false;
            }
        }

        // ---- helpers ----

        private static bool HasChanged(FileState state, bool hasBase, TwoWaySyncEntry baseEntry)
        {
            if (state.Exists)
            {
                return !hasBase || !MatchesBaseline(state, baseEntry); // added or modified
            }
            return hasBase; // deleted (absent + was in baseline)
        }

        private static bool MatchesBaseline(FileState state, TwoWaySyncEntry entry) =>
            state.Size == entry.Size && Math.Abs(state.Mtime.Ticks - entry.WriteTimeUtcTicks) <= WriteTimeTolerance.Ticks;

        private static bool SameState(FileState a, FileState b) =>
            a.Size == b.Size && Math.Abs(a.Mtime.Ticks - b.Mtime.Ticks) <= WriteTimeTolerance.Ticks;

        private static TwoWaySyncEntry BaselineOf(FileState state) => new(state.Mtime.Ticks, state.Size);

        private static FileState ReadState(IBackupFileSystem fs, string path) =>
            fs.FileExists(path)
                ? new FileState(true, fs.GetLastWriteTimeUtc(path), fs.GetFileSize(path))
                : FileState.Absent;

        private async Task<IReadOnlyList<string>?> ListFilesAsync(IBackupFileSystem fs, string dir, IOperationLogger log, BackupResult result)
        {
            try
            {
                return fs.DirectoryExists(dir) ? fs.GetFiles(dir) : [];
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to access folder '{dir}'", ex);
                return null;
            }
        }

        private static IReadOnlyList<string> ListDirsSafe(IBackupFileSystem fs, string dir)
        {
            try
            {
                return fs.DirectoryExists(dir) ? fs.GetDirectories(dir) : [];
            }
            catch
            {
                return [];
            }
        }

        // In-scope file names on a side, excluding (and sweeping) leftover crash-safe temp files.
        private static HashSet<string> InScopeNames(IReadOnlyList<string> files, IBackupFileSystem fs, BackupFilter filter, IReadOnlyList<string> ancestors)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                var name = Path.GetFileName(path)!;
                if (IsCrashSafeTempName(name))
                {
                    TryDeleteTemp(fs, path); // leftover from an interrupted run — never real content
                    continue;
                }
                if (filter.IsFileInScope(name, ancestors))
                {
                    names.Add(name);
                }
            }
            return names;
        }

        private static IEnumerable<string> InScopeNamesSafe(IBackupFileSystem fs, string dir, BackupFilter filter, IReadOnlyList<string> ancestors)
        {
            IReadOnlyList<string> files;
            try
            {
                files = fs.DirectoryExists(dir) ? fs.GetFiles(dir) : [];
            }
            catch
            {
                yield break;
            }

            foreach (var path in files)
            {
                var name = Path.GetFileName(path)!;
                if (!IsCrashSafeTempName(name) && filter.IsFileInScope(name, ancestors))
                {
                    yield return name;
                }
            }
        }

        private static string MakeKey(IReadOnlyList<string> ancestors, string name) =>
            ancestors.Count == 0 ? name : string.Join('/', ancestors) + "/" + name;

        // "report.docx" -> "report (conflict 2026-07-04 100502).docx"; local time, second precision.
        private static string BuildConflictName(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            return $"{stem} (conflict {DateTimeOffset.Now:yyyy-MM-dd HHmmss}){ext}";
        }

        private static void TryStampWriteTime(IBackupFileSystem fs, string path, DateTime sourceTime)
        {
            if (sourceTime >= MinFileTimeUtc)
            {
                fs.SetLastWriteTimeUtc(path, sourceTime);
            }
        }

        private static long TrySize(IBackupFileSystem fs, string path)
        {
            try
            {
                return fs.GetFileSize(path);
            }
            catch
            {
                return 0;
            }
        }

        private static string CrashSafeTempName(string fileName) => $".{fileName}.tmp";

        private static bool IsCrashSafeTempName(string fileName) =>
            fileName.Length > 5 && fileName[0] == '.' && fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        private static void TryDeleteTemp(IBackupFileSystem fs, string tempPath)
        {
            try
            {
                if (fs.FileExists(tempPath))
                {
                    fs.DeleteFile(tempPath);
                }
            }
            catch
            {
                // Best-effort — never let cleanup mask the real work.
            }
        }

        private readonly record struct FileState(bool Exists, DateTime Mtime, long Size)
        {
            public static FileState Absent => new(false, default, 0);
        }

        private sealed record SyncContext(
            IBackupFileSystem LeftFs,
            IBackupFileSystem RightFs,
            TwoWaySyncItem Item,
            BackupFilter Filter,
            IReadOnlyDictionary<string, TwoWaySyncEntry> Baseline,
            Dictionary<string, TwoWaySyncEntry> NewBaseline,
            Action<string?>? OnCurrentFile);
    }
}
