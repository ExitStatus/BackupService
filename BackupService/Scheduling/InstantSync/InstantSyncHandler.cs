using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Profiles;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Handles a manual "Run now" of an <see cref="ProfileType.InstantSync"/> profile: a one-off full
    /// source→target reconcile of every item. Live syncing is driven separately by
    /// <see cref="InstantSyncWatcherService"/>; this is the on-demand catch-up path. Each item is
    /// reconciled by reusing the existing <see cref="IFolderPairSynchronizer"/> over a transient
    /// <see cref="FolderPair"/> (instant sync is source-authoritative, so it uses
    /// <see cref="OverwriteBehaviour.AlwaysOverwrite"/>). Owns the single operation log for the run.
    /// </summary>
    public sealed class InstantSyncHandler(
        IOperationLogFactory operationLogFactory,
        IFolderPairSynchronizer synchronizer,
        IProfileStatusService statusService,
        IBackupRunRecorder runRecorder,
        ILogger<InstantSyncHandler> logger) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.InstantSync;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            // "Run now" runs are prefixed [Manual] in the log to distinguish them from scheduled ones.
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Instant Sync Handler"
            var total = new BackupResult();
            var fatal = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.InstantSyncItems.Count} instant sync item(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            try
            {
                if (profile.InstantSyncItems.Count == 0)
                {
                    await log.AppendAsync("No instant sync items configured.");
                }
                else
                {
                    foreach (var item in profile.InstantSyncItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await RunItemAsync(item, log, total, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                // Catastrophic failure (e.g. cancellation, or something outside an item's own try).
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "InstantSyncHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);
                var outcome = fatal ? RunOutcome.Failed
                    : total.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : total.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;

                // Record the structured run row before the summary write (whose ILogWatcher.Notify
                // the dashboard refreshes on) so the new row is visible when the dashboard reloads.
                await runRecorder.RecordAsync(
                    profile.Id, Type, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                    total, outcome, log.OperationLogId, CancellationToken.None);

                var counts = $"{total.Copied} copied, {total.Updated} updated, {total.Deleted} deleted";
                var (summary, level) = outcome switch
                {
                    RunOutcome.Failed => ($"{handlerName} failed in {duration}", OperationLogLevel.Error),
                    RunOutcome.CompletedWithErrors => ($"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}", OperationLogLevel.Error),
                    RunOutcome.CompletedWithWarnings => ($"{handlerName} completed with {total.Warnings} warning(s) in {duration} — {counts}", OperationLogLevel.Warning),
                    _ => ($"{handlerName} ran successfully in {duration} — {counts}", OperationLogLevel.Info),
                };
                await log.SetSummaryAsync(summary, level);
            }
        }

        private async Task RunItemAsync(InstantSyncItem item, IOperationLogger log, BackupResult total, CancellationToken cancellationToken)
        {
            await log.AppendAsync($"Instant sync '{item.Name}': {item.SourceFolder} -> {item.TargetFolder}");

            // Reuse the folder-pair sync engine for the full reconcile. Source-authoritative → always overwrite.
            var pair = new FolderPair
            {
                Name = item.Name,
                SourceFolder = item.SourceFolder,
                TargetFolder = item.TargetFolder,
                SourceConnectionId = item.SourceConnectionId,
                TargetConnectionId = item.TargetConnectionId,
                AllowDeletions = item.AllowDeletions,
                IncludeSubFolders = item.IncludeSubFolders,
                OverwriteBehaviour = OverwriteBehaviour.AlwaysOverwrite,
            };

            try
            {
                total.Add(await synchronizer.SyncAsync(pair, log, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure for this item — record it and carry on with the others.
                total.Errors++;
                await log.ErrorAsync($"Instant sync '{item.Name}' failed", ex);
            }
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
