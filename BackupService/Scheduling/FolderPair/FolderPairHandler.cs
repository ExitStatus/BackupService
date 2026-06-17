using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
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
        ILogger<FolderPairHandler> logger) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.FolderPair;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            // "Run now" runs are prefixed [Manual] in the log to distinguish them from scheduled ones.
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Folder Pairs Handler"
            var total = new BackupResult();
            var fatal = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.FolderPairs.Count} folder pair(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            try
            {
                if (profile.FolderPairs.Count == 0)
                {
                    await log.AppendAsync("No folder pairs configured.");
                }
                else
                {
                    foreach (var pair in profile.FolderPairs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await RunPairAsync(pair, log, total, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                // Catastrophic failure (e.g. cancellation, or something outside a pair's own try).
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "FolderPairHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                if (fatal)
                {
                    await log.SetSummaryAsync($"{handlerName} failed in {duration}", OperationLogLevel.Error);
                }
                else
                {
                    var counts = $"{total.Copied} copied, {total.Updated} updated, {total.Deleted} deleted";
                    await log.SetSummaryAsync(
                        total.Errors == 0
                            ? $"{handlerName} ran successfully in {duration} — {counts}"
                            : $"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}",
                        total.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
                }
            }
        }

        private async Task RunPairAsync(FolderPair pair, IOperationLogger log, BackupResult total, CancellationToken cancellationToken)
        {
            await SetPairStatusAsync(pair.Id, FolderPairStatus.Running, lastRunStatus: null, cancellationToken);
            await log.AppendAsync($"Folder pair '{pair.Name}': {pair.SourceFolder} -> {pair.TargetFolder}");

            BackupResult result;
            try
            {
                result = await synchronizer.SyncAsync(pair, log, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Mark the pair failed, then let cancellation bubble to the handler's catch.
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
