using BackupService.Scheduling;

namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Background service that fires the automatic database backup on its cron schedule — a trimmed,
    /// single-entry <see cref="BackupSchedulerService"/> (whose public cron statics it reuses), in the
    /// same three-role registration (singleton, <see cref="IDatabaseBackupScheduler"/>, hosted service).
    /// The settings panel calls <see cref="SyncAsync"/> after every save so changes take effect
    /// immediately; the panel also owns that call because this service already depends on
    /// <see cref="IDatabaseBackupService"/> (the reverse dependency would be a DI cycle).
    /// </summary>
    public sealed class DatabaseBackupSchedulerService(
        IDatabaseBackupService backupService,
        ILogger<DatabaseBackupSchedulerService> logger) : BackgroundService, IDatabaseBackupScheduler
    {
        // The longest the loop sleeps before re-evaluating; wake-on-change handles edits promptly.
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

        private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
        private readonly object _lock = new();
        private readonly SemaphoreSlim _wake = new(0, 1);

        private string? _cron;
        private DateTimeOffset? _nextRun;
        private CancellationToken _stoppingToken;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            await SyncAsync(stoppingToken);
            logger.LogInformation("Database backup scheduler started (next run: {NextRun}).", _nextRun?.ToString() ?? "none");

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = FireDueAndComputeWait();

                try
                {
                    await _wake.WaitAsync(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Database backup scheduler stopping.");
        }

        public async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            var settings = await backupService.GetSettingsAsync(cancellationToken);

            lock (_lock)
            {
                _cron = settings.Enabled ? settings.Schedule : null;
                _nextRun = BackupSchedulerService.GetNextOccurrence(_cron, DateTimeOffset.Now, _timeZone);
            }

            Wake();
        }

        /// <summary>Diagnostic/test helper: whether an automatic backup is currently scheduled.</summary>
        public bool IsScheduled
        {
            get
            {
                lock (_lock)
                {
                    return _nextRun is not null;
                }
            }
        }

        private TimeSpan FireDueAndComputeWait()
        {
            var now = DateTimeOffset.Now;
            var fire = false;

            lock (_lock)
            {
                if (_nextRun is { } next && next <= now)
                {
                    fire = true;
                    _nextRun = BackupSchedulerService.GetNextOccurrence(_cron, now, _timeZone);
                }
            }

            if (fire)
            {
                // Fire on its own task so a slow backup never blocks the scheduling loop. RunBackupAsync
                // owns its single-instance gate and its operation log, so fire-and-forget is safe.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await backupService.RunBackupAsync(manual: false, _stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unhandled error running the scheduled database backup.");
                    }
                });
            }

            DateTimeOffset? soonest;
            lock (_lock)
            {
                soonest = _nextRun;
            }

            if (soonest is null)
            {
                return MaxWait;
            }

            var wait = soonest.Value - DateTimeOffset.Now;
            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }
            return wait < MaxWait ? wait : MaxWait;
        }

        private void Wake()
        {
            // Release only when not already signalled, so the bounded semaphore never overflows.
            if (_wake.CurrentCount == 0)
            {
                try
                {
                    _wake.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Raced with another Wake() — already signalled, which is all we wanted.
                }
            }
        }

        public override void Dispose()
        {
            _wake.Dispose();
            base.Dispose();
        }
    }
}
