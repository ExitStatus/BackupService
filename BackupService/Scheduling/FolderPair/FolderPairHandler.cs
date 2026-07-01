using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Notifications;
using BackupService.Profiles;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Handles <see cref="ProfileType.FolderPair"/> profiles: mirrors each folder pair's source into
    /// its target via <see cref="IFolderPairSynchronizer"/>. Owns the single operation log for the run
    /// (one header rewritten to a summary in a <c>finally</c>) and maintains each pair's persisted
    /// <see cref="FolderPair.Status"/>/<see cref="FolderPair.LastRunStatus"/>. Per-file errors are
    /// logged (escalating the header to Error) without aborting the run; only a catastrophic failure
    /// sets the profile status to Error and re-throws.
    /// </summary>
    public sealed class FolderPairHandler(
        IOperationLogFactory operationLogFactory,
        IFolderPairSynchronizer synchronizer,
        IDatabaseContextFactory contextFactory,
        IProfileStatusService statusService,
        IBackupRunRecorder runRecorder,
        ILogger<FolderPairHandler> logger,
        IDesktopNotifier? notifier = null) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.FolderPair;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            // "Run now" runs are prefixed [Manual] in the log to distinguish them from scheduled ones.
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Folder Pairs Handler"
            var total = new BackupResult();
            var fatal = false;
            var cancelled = false;
            var disconnected = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.FolderPairs.Count} folder pair(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            if (profile.NotificationsEnabled && profile.NotifyOnStart)
            {
                notifier?.NotifyBackupStarted(profile.Name, Type);
            }

            try
            {
                if (profile.FolderPairs.Count == 0)
                {
                    await log.AppendAsync("No folder pairs configured.");
                }
                else
                {
                    // Pre-count each pair's files so the grid/progress window can show a per-step and overall
                    // "{percent}%". Each folder pair is one step. Counting is best-effort: a failure (e.g. an
                    // unreachable source) leaves that step at 0 — the sync itself reports the real error.
                    var steps = new List<(string Name, int Count)>();
                    foreach (var pair in profile.FolderPairs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = 0;
                        try
                        {
                            count = await synchronizer.CountFilesAsync(pair, profile.SourceConnectionId, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Best-effort — uncounted files just won't move the bar.
                        }
                        steps.Add((pair.Name, count));
                    }

                    statusService.SetProgress(profile.Id, 0);
                    var progress = new ProfileProgressReporter(statusService, profile.Id, steps);

                    var stepIndex = 0;
                    foreach (var pair in profile.FolderPairs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress.BeginStep(stepIndex++);
                        await RunPairAsync(pair, profile.SourceConnectionId, profile.TargetConnectionId, log, total, progress, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped on request (the Stop button or shutdown). Not a failure — log it as a
                // warning and leave the profile status alone (the runner returns it to Idle so it
                // waits for its next scheduled run).
                cancelled = true;
                await log.AppendAsync(OperationLogLevel.Warning, "Run cancelled — stopped before completion.");
                throw;
            }
            catch (EndpointUnavailableException ex)
            {
                // The source endpoint went away mid-run (e.g. a USB camera switched off / unplugged). Stop fast
                // and clean — partial results so far are valid. Treat it like a cancellation (a warning, profile
                // returns to Idle) rather than a failure, and don't re-throw so the run winds down through the
                // finally. Files already copied are intact; nothing partial is left behind.
                disconnected = true;
                await log.AppendAsync(OperationLogLevel.Warning, $"Source device disconnected — run stopped. {ex.Message}");
            }
            catch (Exception ex)
            {
                // Catastrophic failure (something outside a pair's own try).
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "FolderPairHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);
                var outcome = cancelled || disconnected ? RunOutcome.CompletedWithWarnings
                    : fatal ? RunOutcome.Failed
                    : total.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : total.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;

                // Record the structured run row before the summary write (whose ILogWatcher.Notify
                // the dashboard refreshes on) so the new row is visible when the dashboard reloads.
                await runRecorder.RecordAsync(
                    profile.Id, Type, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                    total, outcome, log.OperationLogId, CancellationToken.None);

                var counts = $"{total.Copied} copied, {total.Updated} updated, {total.Deleted} deleted";
                var (summary, level) = cancelled
                    ? ($"{handlerName} was cancelled after {duration} — {counts}", OperationLogLevel.Warning)
                    : disconnected
                    ? ($"{handlerName} stopped after {duration} — source device disconnected — {counts}", OperationLogLevel.Warning)
                    : outcome switch
                    {
                        RunOutcome.Failed => ($"{handlerName} failed in {duration}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithErrors => ($"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithWarnings => ($"{handlerName} completed with {total.Warnings} warning(s) in {duration} — {counts}", OperationLogLevel.Warning),
                        _ => ($"{handlerName} ran successfully in {duration} — {counts}", OperationLogLevel.Info),
                    };
                await log.SetSummaryAsync(summary, level);

                // Desktop notification for the completed run (a no-op unless on Windows with notifications on).
                // A cancelled or disconnected run isn't a completion, so it's skipped; honour the profile's settings.
                if (!cancelled && !disconnected && profile.NotificationsEnabled && profile.NotifyOnComplete)
                {
                    notifier?.NotifyBackupCompleted(profile.Name, Type, outcome);
                }
            }
        }

        private async Task RunPairAsync(FolderPair pair, int? sourceConnectionId, int? targetConnectionId, IOperationLogger log, BackupResult total, IProgress<int> progress, CancellationToken cancellationToken)
        {
            await SetPairStatusAsync(pair.Id, FolderPairStatus.Running, lastRunStatus: null, cancellationToken);
            await log.AppendAsync($"Folder pair '{pair.Name}': {pair.SourceFolder} -> {pair.TargetFolder}");

            BackupResult result;
            try
            {
                result = await synchronizer.SyncAsync(pair, sourceConnectionId, targetConnectionId, log, cancellationToken, progress);
            }
            catch (OperationCanceledException)
            {
                // Mark the pair failed, then let cancellation bubble to the handler's catch.
                await SetPairStatusAsync(pair.Id, FolderPairStatus.Idle, FolderPairLastRunStatus.Fail, CancellationToken.None);
                throw;
            }
            catch (EndpointUnavailableException)
            {
                // Source endpoint gone — mark the pair failed and let it bubble to the handler so the whole run
                // aborts (the remaining pairs share the same dead device).
                await SetPairStatusAsync(pair.Id, FolderPairStatus.Idle, FolderPairLastRunStatus.Fail, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure for this pair — record it and carry on with the others.
                total.Errors++;
                await log.ErrorAsync($"Folder pair '{pair.Name}' failed", ex);
                await SetPairStatusAsync(pair.Id, FolderPairStatus.Idle, FolderPairLastRunStatus.Fail, CancellationToken.None);
                return;
            }

            total.Add(result);
            var lastRun = result.Errors == 0 ? FolderPairLastRunStatus.Success : FolderPairLastRunStatus.Fail;
            await SetPairStatusAsync(pair.Id, FolderPairStatus.Idle, lastRun, cancellationToken);
        }

        private async Task SetPairStatusAsync(int pairId, FolderPairStatus status, FolderPairLastRunStatus? lastRunStatus, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            var pair = await db.FolderPairs.FirstOrDefaultAsync(p => p.Id == pairId, cancellationToken);
            if (pair is null)
            {
                return;
            }

            pair.Status = status;
            if (lastRunStatus is not null)
            {
                pair.LastRunStatus = lastRunStatus.Value;
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
