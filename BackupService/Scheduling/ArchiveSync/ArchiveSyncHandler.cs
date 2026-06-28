using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Notifications;
using BackupService.Profiles;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Handles <see cref="ProfileType.ArchiveSync"/> profiles: for each item builds a timestamped ZIP
    /// of the source and applies its retention policy via <see cref="IArchiveSyncProcessor"/>. Owns the
    /// single operation log for the run (one header rewritten to a summary in a <c>finally</c>). A
    /// per-item failure is logged without aborting the run; only a catastrophic failure sets the
    /// profile status to Error and re-throws. After an item creates an archive, its persisted
    /// <see cref="ArchiveSyncItem.RunCount"/> is advanced (it drives the GFS promotion cadence).
    /// </summary>
    public sealed class ArchiveSyncHandler(
        IOperationLogFactory operationLogFactory,
        IArchiveSyncProcessor processor,
        IDatabaseContextFactory contextFactory,
        IProfileStatusService statusService,
        IBackupRunRecorder runRecorder,
        ILogger<ArchiveSyncHandler> logger,
        IRunCompletionNotifier? notifier = null) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.ArchiveSync;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            // "Run now" runs are prefixed [Manual] in the log to distinguish them from scheduled ones.
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Archive Sync Handler"
            var total = new BackupResult();
            var fatal = false;
            var cancelled = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.ArchiveSyncItems.Count} archive(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            try
            {
                if (profile.ArchiveSyncItems.Count == 0)
                {
                    await log.AppendAsync("No archives configured.");
                }
                else
                {
                    // Each item contributes an equal slice of the run; within a slice the processor reports a
                    // 0..1 completion fraction (75% files-zipped, 25% bytes-copied) so progress is granular.
                    var totalItems = profile.ArchiveSyncItems.Count;
                    statusService.SetProgress(profile.Id, 0);
                    var completed = 0;
                    foreach (var item in profile.ArchiveSyncItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var itemIndex = completed; // captured for the closure below
                        var itemProgress = new DelegateProgress(fraction =>
                            statusService.SetProgress(profile.Id, (int)((itemIndex + Math.Clamp(fraction, 0, 1)) * 100 / totalItems)));
                        await RunItemAsync(item, log, total, itemProgress, cancellationToken);
                        completed++;
                        statusService.SetProgress(profile.Id, completed * 100 / totalItems);
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
            catch (Exception ex)
            {
                // Catastrophic failure (something outside an item's own try).
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "ArchiveSyncHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);
                var outcome = cancelled ? RunOutcome.CompletedWithWarnings
                    : fatal ? RunOutcome.Failed
                    : total.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : total.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;

                // Record the structured run row before the summary write (whose ILogWatcher.Notify
                // the dashboard refreshes on) so the new row is visible when the dashboard reloads.
                await runRecorder.RecordAsync(
                    profile.Id, Type, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                    total, outcome, log.OperationLogId, CancellationToken.None);

                var counts = $"{total.Copied} archive(s) created, {total.Deleted} pruned";
                var (summary, level) = cancelled
                    ? ($"{handlerName} was cancelled after {duration} — {counts}", OperationLogLevel.Warning)
                    : outcome switch
                    {
                        RunOutcome.Failed => ($"{handlerName} failed in {duration}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithErrors => ($"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithWarnings => ($"{handlerName} completed with {total.Warnings} warning(s) in {duration} — {counts}", OperationLogLevel.Warning),
                        _ => ($"{handlerName} ran successfully in {duration} — {counts}", OperationLogLevel.Info),
                    };
                await log.SetSummaryAsync(summary, level);

                // Desktop notification for the completed run (a no-op unless on Windows with notifications on).
                // A cancelled run isn't a completion, so it's skipped.
                if (!cancelled)
                {
                    notifier?.NotifyBackupCompleted(profile.Name, Type, outcome);
                }
            }
        }

        private async Task RunItemAsync(ArchiveSyncItem item, IOperationLogger log, BackupResult total, IProgress<double> progress, CancellationToken cancellationToken)
        {
            await log.AppendAsync($"Archive '{item.Name}': {item.SourceFolder} -> {item.TargetFolder}");

            var runIndex = item.RunCount + 1;

            BackupResult result;
            try
            {
                result = await processor.CreateArchiveAsync(item, runIndex, DateTime.Now, log, cancellationToken, progress);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure for this item — record it and carry on with the others.
                total.Errors++;
                await log.ErrorAsync($"Archive '{item.Name}' failed", ex);
                return;
            }

            total.Add(result);

            // Advance the run counter only when an archive was actually created (so the GFS cadence
            // tracks archives created, not failed attempts).
            if (result.Copied > 0)
            {
                await PersistRunCountAsync(item.Id, runIndex, cancellationToken);
            }
        }

        private async Task PersistRunCountAsync(int itemId, int runCount, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            var item = await db.ArchiveSyncItems.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
            if (item is null)
            {
                return;
            }

            item.RunCount = runCount;
            await db.SaveChangesAsync(cancellationToken);
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";

        /// <summary>
        /// A synchronous <see cref="IProgress{T}"/> that invokes the handler inline (unlike
        /// <see cref="Progress{T}"/>, which posts to a captured context — there's none in a background
        /// service, so reports would run out of order on the thread pool).
        /// </summary>
        private sealed class DelegateProgress(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }
    }
}
