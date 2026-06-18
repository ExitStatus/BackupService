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
        ILogger<ArchiveSyncHandler> logger) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.ArchiveSync;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            // "Run now" runs are prefixed [Manual] in the log to distinguish them from scheduled ones.
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Archive Sync Handler"
            var total = new BackupResult();
            var fatal = false;

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
                    foreach (var item in profile.ArchiveSyncItems)
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
                logger.LogError(ex, "ArchiveSyncHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
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
                    var counts = $"{total.Copied} archive(s) created, {total.Deleted} pruned";
                    await log.SetSummaryAsync(
                        total.Errors == 0
                            ? $"{handlerName} ran successfully in {duration} — {counts}"
                            : $"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}",
                        total.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
                }
            }
        }

        private async Task RunItemAsync(ArchiveSyncItem item, IOperationLogger log, BackupResult total, CancellationToken cancellationToken)
        {
            await log.AppendAsync($"Archive '{item.Name}': {item.SourceFolder} -> {item.TargetFolder}");

            var runIndex = item.RunCount + 1;

            BackupResult result;
            try
            {
                result = await processor.CreateArchiveAsync(item, runIndex, DateTime.Now, log, cancellationToken);
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
    }
}
