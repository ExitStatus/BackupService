using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Notifications;
using BackupService.Profiles;

namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>
    /// Handles <see cref="ProfileType.TwoWaySync"/> profiles: reconciles each item's two folders in both
    /// directions via <see cref="ITwoWaySyncEngine"/>. Scheduled like OneWaySync/ArchiveSync. Owns the single
    /// operation log for the run (one header rewritten to a summary in a <c>finally</c>) and records one
    /// <see cref="BackupRun"/> row. Per-item errors are logged without aborting the run; only a catastrophic
    /// failure sets the profile status to Error and re-throws.
    /// </summary>
    public sealed class TwoWaySyncHandler(
        IOperationLogFactory operationLogFactory,
        ITwoWaySyncEngine engine,
        IProfileStatusService statusService,
        IBackupRunRecorder runRecorder,
        ILogger<TwoWaySyncHandler> logger,
        IDesktopNotifier? notifier = null) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.TwoWaySync;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Two Way Sync Handler"
            var total = new BackupResult();
            var fatal = false;
            var cancelled = false;
            var disconnected = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.TwoWaySyncItems.Count} item(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            if (profile.NotificationsEnabled && profile.NotifyOnStart)
            {
                notifier?.NotifyBackupStarted(profile.Name, Type);
            }

            try
            {
                if (profile.TwoWaySyncItems.Count == 0)
                {
                    await log.AppendAsync("No two way sync items configured.");
                }
                else
                {
                    // Pre-count each item's files (union of both sides) so the grid/progress window can show a
                    // per-step and overall percent. Each item is one step; counting is best-effort.
                    var steps = new List<(string Name, int Count)>();
                    foreach (var item in profile.TwoWaySyncItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = 0;
                        try
                        {
                            count = await engine.CountFilesAsync(item, profile.SourceConnectionId, profile.TargetConnectionId, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Best-effort — uncounted files just won't move the bar.
                        }
                        steps.Add((item.Name, count));
                    }

                    statusService.SetProgress(profile.Id, 0);
                    var progress = new ProfileProgressReporter(statusService, profile.Id, steps);

                    var stepIndex = 0;
                    foreach (var item in profile.TwoWaySyncItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress.BeginStep(stepIndex++);
                        await RunItemAsync(item, profile.SourceConnectionId, profile.TargetConnectionId, log, total, progress, progress.ReportFile, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                await log.AppendAsync(OperationLogLevel.Warning, "Run cancelled — stopped before completion.");
                throw;
            }
            catch (EndpointUnavailableException ex)
            {
                // A side went away mid-run (e.g. a USB drive unplugged). Stop fast and clean — work done so far
                // is valid, and the baseline for the in-flight item wasn't saved, so it re-reconciles next run.
                disconnected = true;
                await log.AppendAsync(OperationLogLevel.Warning, $"A device disconnected — run stopped. {ex.Message}");
            }
            catch (Exception ex)
            {
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "TwoWaySyncHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
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

                await runRecorder.RecordAsync(
                    profile.Id, Type, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                    total, outcome, log.OperationLogId, CancellationToken.None);

                var counts = $"{total.Copied} copied, {total.Updated} updated, {total.Deleted} deleted";
                var (summary, level) = cancelled
                    ? ($"{handlerName} was cancelled after {duration} — {counts}", OperationLogLevel.Warning)
                    : disconnected
                    ? ($"{handlerName} stopped after {duration} — a device disconnected — {counts}", OperationLogLevel.Warning)
                    : outcome switch
                    {
                        RunOutcome.Failed => ($"{handlerName} failed in {duration}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithErrors => ($"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}", OperationLogLevel.Error),
                        RunOutcome.CompletedWithWarnings => ($"{handlerName} completed with {total.Warnings} warning(s) in {duration} — {counts}", OperationLogLevel.Warning),
                        _ => ($"{handlerName} ran successfully in {duration} — {counts}", OperationLogLevel.Info),
                    };
                await log.SetSummaryAsync(summary, level);

                if (!cancelled && !disconnected && profile.NotificationsEnabled && profile.NotifyOnComplete)
                {
                    notifier?.NotifyBackupCompleted(profile.Name, Type, outcome);
                }
            }
        }

        private async Task RunItemAsync(TwoWaySyncItem item, int? sourceConnectionId, int? targetConnectionId, IOperationLogger log, BackupResult total, IProgress<int> progress, Action<string?>? onCurrentFile, CancellationToken cancellationToken)
        {
            await log.AppendAsync($"Two way sync '{item.Name}': {item.SourceFolder} <-> {item.TargetFolder}");

            BackupResult result;
            try
            {
                result = await engine.SyncAsync(item, sourceConnectionId, targetConnectionId, log, cancellationToken, progress, onCurrentFile);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (EndpointUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                total.Errors++;
                await log.ErrorAsync($"Two way sync '{item.Name}' failed", ex);
                return;
            }

            total.Add(result);
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
