using BackupService.Database;
using BackupService.Profiles;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Background service that fires each enabled profile's backup work on its cron schedule.
    /// Also the <see cref="IBackupScheduler"/> the rest of the app calls to keep the live schedule
    /// in sync with profile changes. Registered once and shared in all three roles (singleton,
    /// <see cref="IBackupScheduler"/>, hosted service).
    ///
    /// Schedules are evaluated against local wall-clock time via Cronos
    /// (<see cref="CronExpression.GetNextOccurrence(DateTimeOffset, TimeZoneInfo, bool)"/>), which
    /// is DST-correct. Due jobs are launched concurrently (one slow backup never blocks another),
    /// and a profile is never run twice at once.
    /// </summary>
    public sealed class BackupSchedulerService(
        IDatabaseContextFactory contextFactory,
        IBackupRunner runner,
        IProfileStatusService statusService,
        ILogger<BackupSchedulerService> logger) : BackgroundService, IBackupScheduler
    {
        // The longest the loop sleeps before re-evaluating, even when the next run is further off.
        // Wake-on-change handles edits promptly; this cap just guards against system clock changes.
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

        private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
        private readonly Dictionary<int, ScheduledEntry> _entries = new();
        private readonly object _entriesLock = new();
        private readonly SemaphoreSlim _wake = new(0, 1);

        private CancellationToken _stoppingToken;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            logger.LogInformation("Backup scheduler started.");

            await LoadAllAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = FireDueAndComputeWait();

                try
                {
                    // Wake early when a profile changes (SyncAsync releases the semaphore), or
                    // after the delay elapses to fire whatever is due.
                    await _wake.WaitAsync(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Backup scheduler stopping.");
        }

        public async Task SyncAsync(int profileId, CancellationToken cancellationToken = default)
        {
            var info = await ReadProfileScheduleAsync(profileId, cancellationToken);

            lock (_entriesLock)
            {
                _entries.Remove(profileId);

                if (info is { Enabled: true } && TryParse(info.Schedule) is { } cron)
                {
                    var next = cron.GetNextOccurrence(DateTimeOffset.Now, _timeZone);
                    if (next.HasValue)
                    {
                        _entries[profileId] = new ScheduledEntry(cron, next.Value);
                    }
                }
            }

            Wake();
        }

        /// <summary>Diagnostic/test helper: whether the profile currently has a live schedule entry.</summary>
        public bool IsScheduled(int profileId)
        {
            lock (_entriesLock)
            {
                return _entries.ContainsKey(profileId);
            }
        }

        /// <summary>
        /// Next occurrence of <paramref name="cron"/> strictly after <paramref name="from"/> in the
        /// given time zone, or null when the cron is null/blank/unparseable. Pure — extracted for tests.
        /// </summary>
        public static DateTimeOffset? GetNextOccurrence(string? cron, DateTimeOffset from, TimeZoneInfo timeZone)
            => TryParse(cron)?.GetNextOccurrence(from, timeZone);

        private async Task LoadAllAsync(CancellationToken cancellationToken)
        {
            List<(int Id, string? Schedule)> scheduled;
            await using (var db = contextFactory.CreateDbContext())
            {
                scheduled = await db.Profiles
                    .AsNoTracking()
                    .Where(p => p.Enabled && p.Schedule != null && p.Schedule != "")
                    .Select(p => new ValueTuple<int, string?>(p.Id, p.Schedule))
                    .ToListAsync(cancellationToken);
            }

            var now = DateTimeOffset.Now;
            lock (_entriesLock)
            {
                _entries.Clear();
                foreach (var (id, schedule) in scheduled)
                {
                    if (TryParse(schedule) is { } cron && cron.GetNextOccurrence(now, _timeZone) is { } next)
                    {
                        _entries[id] = new ScheduledEntry(cron, next);
                    }
                }
            }

            logger.LogInformation("Backup scheduler loaded {Count} scheduled profile(s).", _entries.Count);
        }

        /// <summary>
        /// Fires every entry whose next run has arrived (advancing it to its following occurrence),
        /// and returns how long to wait before the next evaluation.
        /// </summary>
        private TimeSpan FireDueAndComputeWait()
        {
            var now = DateTimeOffset.Now;
            var due = new List<int>();
            DateTimeOffset? soonest = null;

            lock (_entriesLock)
            {
                foreach (var (id, entry) in _entries)
                {
                    if (entry.NextRun <= now)
                    {
                        due.Add(id);
                        var next = entry.Cron.GetNextOccurrence(now, _timeZone);
                        if (next.HasValue)
                        {
                            entry.NextRun = next.Value;
                        }
                    }
                }

                foreach (var entry in _entries.Values)
                {
                    if (soonest is null || entry.NextRun < soonest)
                    {
                        soonest = entry.NextRun;
                    }
                }
            }

            foreach (var id in due)
            {
                FireJob(id);
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

        private void FireJob(int profileId)
        {
            // Only one run per profile at a time — if one is already in progress, don't even start a
            // thread. (The runner re-checks this atomically via TryBeginRun as the authority.)
            if (statusService.IsRunning(profileId))
            {
                logger.LogInformation("Skipping scheduled run for profile {ProfileId}: a run is already in progress.", profileId);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(profileId, _stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error running scheduled backup for profile {ProfileId}.", profileId);
                }
            });
        }

        private async Task<ProfileScheduleInfo?> ReadProfileScheduleAsync(int profileId, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .Where(p => p.Id == profileId)
                .Select(p => new ProfileScheduleInfo(p.Enabled, p.Schedule))
                .FirstOrDefaultAsync(cancellationToken);
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

        private static CronExpression? TryParse(string? cron)
        {
            if (string.IsNullOrWhiteSpace(cron))
            {
                return null;
            }

            try
            {
                return CronExpression.Parse(cron);
            }
            catch (CronFormatException)
            {
                return null;
            }
        }

        public override void Dispose()
        {
            _wake.Dispose();
            base.Dispose();
        }

        private sealed class ScheduledEntry(CronExpression cron, DateTimeOffset nextRun)
        {
            public CronExpression Cron { get; } = cron;

            public DateTimeOffset NextRun { get; set; } = nextRun;
        }

        private sealed record ProfileScheduleInfo(bool Enabled, string? Schedule);
    }
}
