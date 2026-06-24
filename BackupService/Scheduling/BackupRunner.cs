using System.Collections.Concurrent;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IBackupRunner"/>. Loads the profile, brackets the run with
    /// status/operation-log bookkeeping, and dispatches the actual work to the
    /// <see cref="IProfileTypeHandler"/> registered for the profile's <see cref="Profile.Type"/>.
    /// </summary>
    public sealed class BackupRunner : IBackupRunner
    {
        private readonly IDatabaseContextFactory _contextFactory;
        private readonly IOperationLogFactory _operationLogFactory;
        private readonly IProfileStatusService _statusService;
        private readonly ILogger<BackupRunner> _logger;
        private readonly IReadOnlyDictionary<ProfileType, IProfileTypeHandler> _handlers;

        // The cancellation source for each in-progress run, keyed by profile id, so the UI's Stop
        // button (RequestStop) can cancel a run that's already under way.
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

        public BackupRunner(
            IDatabaseContextFactory contextFactory,
            IOperationLogFactory operationLogFactory,
            IProfileStatusService statusService,
            IEnumerable<IProfileTypeHandler> handlers,
            ILogger<BackupRunner> logger)
        {
            _contextFactory = contextFactory;
            _operationLogFactory = operationLogFactory;
            _statusService = statusService;
            _logger = logger;
            _handlers = handlers.ToDictionary(h => h.Type);
        }

        public async Task RunAsync(int profileId, bool manual = false, CancellationToken cancellationToken = default)
        {
            Profile? profile;
            await using (var db = _contextFactory.CreateDbContext())
            {
                profile = await db.Profiles
                    .Include(p => p.FolderPairs).ThenInclude(fp => fp.Filters)
                    .Include(p => p.InstantSyncItems)
                    .Include(p => p.ArchiveSyncItems).ThenInclude(a => a.Filters)
                    .Include(p => p.LightroomArchiveItems)
                    .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
            }

            if (profile is null)
            {
                _logger.LogWarning("Scheduled run skipped: profile {ProfileId} no longer exists.", profileId);
                return;
            }

            // Don't run while an admin has the profile open in an edit/delete dialog.
            if (_statusService.IsLocked(profileId))
            {
                _logger.LogInformation("Scheduled run skipped: profile {ProfileId} is open in an edit/delete dialog.", profileId);
                return;
            }

            if (!_handlers.TryGetValue(profile.Type, out var handler))
            {
                var failureName = manual
                    ? $"[Manual] Backup failed: {profile.Name}"
                    : $"Scheduled backup failed: {profile.Name}";
                var errorLog = await _operationLogFactory.CreateAsync(
                    failureName,
                    OperationLogLevel.Error,
                    profile.Id,
                    cancellationToken);
                await errorLog.AppendAsync($"No handler registered for profile type '{profile.Type}'.");
                _logger.LogError("No handler registered for profile type {ProfileType} (profile {ProfileId}).", profile.Type, profile.Id);
                return;
            }

            // Only one run per profile at a time: bail out if one is already in progress.
            if (!_statusService.TryBeginRun(profile.Id))
            {
                _logger.LogInformation("Scheduled run skipped for profile {ProfileId}: a run is already in progress.", profile.Id);
                return;
            }

            // The handler owns the operation log for the run (one log per run); the runner only
            // tracks status and the last-run timestamp.
            var finalStatus = ProfileStatus.Idle;

            // A linked source so a run can be stopped either by the host shutting down (the passed
            // token) or by the user's Stop button (RequestStop cancels this source).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[profile.Id] = cts;
            try
            {
                await handler.HandleAsync(profile, manual, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Stopped on request (Stop button or shutdown): not a failure. The handler already
                // finished its log as a warning; return the profile to Idle so it waits for its next
                // scheduled run rather than sticking on Error.
                finalStatus = ProfileStatus.Idle;
                _logger.LogInformation("Backup for profile {ProfileId} ({ProfileName}) was cancelled.", profile.Id, profile.Name);
            }
            catch (Exception ex)
            {
                // The handler's own catch-all also set the status to Error.
                finalStatus = ProfileStatus.Error;
                _logger.LogError(ex, "Scheduled backup failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
            }
            finally
            {
                _running.TryRemove(profile.Id, out _);
            }

            // Persist DateLastRun BEFORE flipping the status (the grid reloads on the status change,
            // so the timestamp must already be saved), and never leave the status stuck on Running —
            // a failed stamp must not prevent the terminal status from being set.
            try
            {
                await StampLastRunAsync(profile.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record last-run time for profile {ProfileId}.", profile.Id);
            }

            _statusService.Set(profile.Id, finalStatus);
        }

        public bool RequestStop(int profileId)
        {
            if (!_running.TryGetValue(profileId, out var cts))
            {
                return false;
            }

            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the lookup and the cancel — nothing to stop.
                return false;
            }
        }

        private async Task StampLastRunAsync(int profileId, CancellationToken cancellationToken)
        {
            await using var db = _contextFactory.CreateDbContext();

            var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
            if (profile is null)
            {
                return;
            }

            profile.DateLastRun = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
