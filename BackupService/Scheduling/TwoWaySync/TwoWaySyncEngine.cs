using System.Buffers;
using System.Globalization;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
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
            var endpointKey = EndpointKey(item, sourceConnectionId, targetConnectionId);
            var loaded = await stateStore.LoadAsync(item.Id, endpointKey, cancellationToken);
            var baseline = loaded.Entries;
            var newBaseline = new Dictionary<string, TwoWaySyncEntry>(StringComparer.OrdinalIgnoreCase);

            if (loaded.WasReset)
            {
                await log.AppendAsync(
                    $"Two way sync '{item.Name}': the folders or connections changed since the last sync, so its sync state was reset — this run copies in both directions but deletes nothing.");
            }

            var left = await endpointFactory.ResolveAsync(sourceConnectionId, item.SourceFolder, cancellationToken);
            try
            {
                var right = await endpointFactory.ResolveAsync(targetConnectionId, item.TargetFolder, cancellationToken);
                try
                {
                    // Two-way sync writes to both sides. A read-only side (an MTP phone or camera) fails every copy
                    // and deletion towards it, every run — refuse the item up front instead.
                    if (left.FileSystem.IsReadOnly || right.FileSystem.IsReadOnly)
                    {
                        result.Errors++;
                        await log.AppendAsync(OperationLogLevel.Error,
                            $"Two way sync '{item.Name}': the {(left.FileSystem.IsReadOnly ? "source" : "target")} is read-only (a portable device), and two-way sync has to write to both sides. Use a one way sync for it.");
                        return result; // baseline left as it was
                    }

                    // A side whose folder can't be found would otherwise read as empty — and against a baseline that
                    // lists files, "empty" means "everything was deleted there", which would then be propagated to
                    // the other side. An unplugged drive, a renamed folder or a share that denies access must never
                    // do that, so once anything has been synced both folders have to be present to run at all.
                    if (baseline.Count > 0
                        && (await MissingRootAsync((left.FileSystem, left.BasePath, item.SourceFolder), log, result)
                            || await MissingRootAsync((right.FileSystem, right.BasePath, item.TargetFolder), log, result)))
                    {
                        return result; // baseline left as it was
                    }

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
            await stateStore.SaveAsync(item.Id, endpointKey, newBaseline, cancellationToken);
            return result;
        }

        // Identifies the folder pair a baseline describes: each side's connection (or local) and folder. Folders are
        // compared case-insensitively and without trailing separators, so a cosmetic edit doesn't reset the state.
        internal static string EndpointKey(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId) =>
            $"{Side(sourceConnectionId, item.SourceFolder)}|{Side(targetConnectionId, item.TargetFolder)}";

        private static string Side(int? connectionId, string? folder) =>
            $"{connectionId?.ToString(CultureInfo.InvariantCulture) ?? "local"}:{(folder ?? string.Empty).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant()}";

        // True (and logged as an error) when a side's root folder isn't there.
        private static async Task<bool> MissingRootAsync((IBackupFileSystem Fs, string BasePath, string? Configured) side, IOperationLogger log, BackupResult result)
        {
            if (side.Fs.DirectoryExists(side.BasePath))
            {
                return false;
            }

            result.Errors++;
            await log.ErrorAsync(
                $"Folder '{side.Configured}' was not found, so nothing was synced (a missing folder must not be mistaken for every file in it having been deleted). Reconnect or restore it, or point the item at the right folder.");
            return true;
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
            foreach (var d in ListDirsSafe(leftFs, leftDir)) subs.Add(PathHelper.GetLeafName(d));
            foreach (var d in ListDirsSafe(rightFs, rightDir)) subs.Add(PathHelper.GetLeafName(d));

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
                // A side couldn't be listed — skip this subtree (already logged), keeping its baseline for next run.
                KeepBaselineUnder(ancestors, ctx, includeFilesHere: true);
                return;
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

            // A side whose sub-folders can't be listed must not read as having none (its sub-trees would then look
            // deleted), so a listing failure skips this folder's sub-trees for the run — keeping their baselines.
            var leftDirs = await ListDirsAsync(ctx.LeftFs, leftDir, log, result);
            var rightDirs = await ListDirsAsync(ctx.RightFs, rightDir, log, result);
            if (leftDirs is null || rightDirs is null)
            {
                KeepBaselineUnder(ancestors, ctx, includeFilesHere: false);
                return;
            }

            var subs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in leftDirs) subs.Add(PathHelper.GetLeafName(d));
            foreach (var d in rightDirs) subs.Add(PathHelper.GetLeafName(d));

            foreach (var sub in subs)
            {
                if (filter.ExcludesFolder(sub) || filter.ExcludesPath([.. ancestors, sub]))
                {
                    continue;
                }
                await SyncDirectoryAsync(Path.Combine(leftDir, sub), Path.Combine(rightDir, sub), [.. ancestors, sub], ctx, log, result, fileProgress, ct);
            }
        }

        // The previous baseline entry for a file whose fate wasn't settled this run (a failed read, copy or delete)
        // is carried forward, so next run compares against the same state. Dropping it instead would make a one-sided
        // edit look like a both-sides conflict, which SourceWins/TargetWins could then resolve against the edit.
        private static void KeepBaseline(string key, SyncContext ctx)
        {
            if (ctx.Baseline.TryGetValue(key, out var previous))
            {
                ctx.NewBaseline[key] = previous;
            }
        }

        // Carries forward the baseline entries beneath a folder that wasn't (fully) visited this run: those in its
        // sub-folders, plus — with includeFilesHere — the folder's own files.
        private static void KeepBaselineUnder(IReadOnlyList<string> ancestors, SyncContext ctx, bool includeFilesHere)
        {
            var prefix = ancestors.Count == 0 ? string.Empty : string.Join('/', ancestors) + "/";
            foreach (var (key, entry) in ctx.Baseline)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (includeFilesHere || key.IndexOf('/', prefix.Length) >= 0))
                {
                    ctx.NewBaseline[key] = entry;
                }
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
                KeepBaseline(key, ctx); // still undecided — judge it against the same baseline next run
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
                    else
                    {
                        KeepBaseline(key, ctx); // retry the deletion next run rather than resurrect the file
                    }
                }
                // Otherwise not added to the new baseline — the file is gone from both sides.
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
                else
                {
                    KeepBaseline(key, ctx);
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
            catch (ProtectedFileException ex)
            {
                // The target won't rename it (a Google Docs file on Drive) — leave both sides as they are.
                result.Warnings++;
                await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{key}' — {ex.Reason}");
                KeepBaseline(key, ctx);
                return;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Conflict on '{key}' — failed to set aside the target copy", ex);
                KeepBaseline(key, ctx);
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
        // Copied vs Updated. Returns whether the copy succeeded (a skipped/failed copy keeps the previous baseline
        // entry, if any, so it's retried next run against the same state).
        private async Task<bool> CopyAndBaselineAsync(bool destExisted, string key, IBackupFileSystem fromFs, string fromPath, FileState from, IBackupFileSystem toFs, string toDir, string toName, SyncContext ctx, IOperationLogger log, BackupResult result, CancellationToken ct)
        {
            if (!await CopyThroughTempAsync(fromFs, fromPath, from, toFs, toDir, toName, ctx.OnCurrentFile, log, result, ct))
            {
                KeepBaseline(key, ctx);
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

        private async Task<bool> CopyThroughTempAsync(IBackupFileSystem fromFs, string fromPath, FileState from, IBackupFileSystem toFs, string toDir, string toName, Action<string?>? onCurrentFile, IOperationLogger log, BackupResult result, CancellationToken ct)
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
                long written;
                using (var input = fromFs.OpenRead(fromPath, ct))
                using (var output = toFs.OpenWrite(tempPath))
                {
                    written = await CopyCountingAsync(input, output, ct);
                }

                // Never commit a short copy. In a two-way sync a truncated copy stamped with the source's time would
                // later look like the side that changed, and be copied back over the intact original.
                if (from.Size > 0 && written < from.Size)
                {
                    if (SizeChanged(fromFs, fromPath, from.Size))
                    {
                        // The file shrank while it was being read (a save mid-copy) — retry next run, like a locked file.
                        TryDeleteTemp(toFs, tempPath);
                        result.Warnings++;
                        await log.AppendAsync(OperationLogLevel.Warning, $"Skipped '{fromPath}' — it changed while it was being copied (it will be retried on the next run)");
                        return false;
                    }

                    throw new IOException($"Incomplete copy: only {written} of {from.Size} bytes were transferred.");
                }

                TryStampWriteTime(toFs, tempPath, from.Mtime);

                if (toFs.FileExists(destPath))
                {
                    toFs.DeleteFile(destPath);
                }
                toFs.MoveFile(tempPath, destPath, overwrite: false);
                result.BytesCopied += written;
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
            catch (ProtectedFileException ex)
            {
                // The destination side refused to replace its file (a Google Docs file on Drive) — keep it. Not
                // baselined, so it's re-evaluated next run rather than treated as in sync.
                TryDeleteTemp(toFs, tempPath);
                result.Warnings++;
                await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{destPath}' — {ex.Reason}");
                return false;
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
            catch (ProtectedFileException ex)
            {
                // A deletion propagated to a file that side protects (a Google Docs file on Drive) — keep it.
                result.Warnings++;
                await log.AppendAsync(OperationLogLevel.Warning, $"Kept '{key}' — {ex.Reason}");
                return false;
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

        private static FileState ReadState(IBackupFileSystem fs, string path)
        {
            if (!fs.FileExists(path))
            {
                return FileState.Absent;
            }
            var stat = fs.GetFileStat(path); // time + size in one lookup
            return new FileState(true, stat.LastWriteTimeUtc, stat.Size);
        }

        private async Task<IReadOnlyList<string>?> ListDirsAsync(IBackupFileSystem fs, string dir, IOperationLogger log, BackupResult result)
        {
            try
            {
                return fs.DirectoryExists(dir) ? fs.GetDirectories(dir) : [];
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Errors++;
                await log.ErrorAsync($"Failed to list the sub-folders of '{dir}'", ex);
                return null;
            }
        }

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
                var name = PathHelper.GetLeafName(path);
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
                var name = PathHelper.GetLeafName(path);
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

        // Re-reads the size after a short copy: true only when it can be read and is no longer the size the copy
        // started from. A gone endpoint still aborts the run.
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

        // Both sides of a two-way sync are live user folders, so the crash-safe temp needs a name no user or other
        // app would pick: a plain ".{name}.tmp" is also what other tools use for their own in-flight saves, and the
        // leftover-temp sweep would delete those. Only files with this exact suffix are ever swept.
        private const string CrashSafeTempSuffix = ".backupservice.tmp";

        private static string CrashSafeTempName(string fileName) => $".{fileName}{CrashSafeTempSuffix}";

        private static bool IsCrashSafeTempName(string fileName) =>
            fileName.Length > CrashSafeTempSuffix.Length + 1
            && fileName[0] == '.'
            && fileName.EndsWith(CrashSafeTempSuffix, StringComparison.OrdinalIgnoreCase);

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
