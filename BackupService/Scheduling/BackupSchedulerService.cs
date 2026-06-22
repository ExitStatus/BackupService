using BackupService.Database;
using BackupService.Logging;
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
        IOperationLogFactory operationLogFactory,
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
                var (delay, advanced) = FireDueAndComputeWait();

                // Record the new next-run time for every entry that just advanced.
                foreach (var (id, next) in advanced)
                {
                    await PersistNextRunAsync(id, next, stoppingToken);
                }

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

            DateTimeOffset? nextRun = null;
            lock (_entriesLock)
            {
                _entries.Remove(profileId);

                if (info is { Enabled: true } && TryParse(info.Schedule) is { } cron)
                {
                    var next = cron.GetNextOccurrence(DateTimeOffset.Now, _timeZone);
                    if (next.HasValue)
                    {
                        _entries[profileId] = new ScheduledEntry(cron, next.Value);
                        nextRun = next.Value;
                    }
                }
            }

            // Record the upcoming run (or clear it when the profile is unscheduled).
            await PersistNextRunAsync(profileId, nextRun, cancellationToken);

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

        /// <summary>
        /// Whether a profile missed its scheduled run while the service was down and should run immediately on
        /// startup: only when it opted in (<paramref name="handleMissedSync"/>) and its last recorded next-run
        /// time (<paramref name="persistedNextRun"/>) is before <paramref name="now"/>. Pure — extracted for tests.
        /// </summary>
        public static bool ShouldCatchUp(bool handleMissedSync, DateTimeOffset? persistedNextRun, DateTimeOffset now)
            => handleMissedSync && persistedNextRun is { } due && due < now;

        private async Task LoadAllAsync(CancellationToken cancellationToken)
        {
            List<ProfileScheduleState> scheduled;
            await using (var db = contextFactory.CreateDbContext())
            {
                scheduled = await db.Profiles
                    .AsNoTracking()
                    .Where(p => p.Enabled && p.Schedule != null && p.Schedule != "")
                    .Select(p => new ProfileScheduleState(p.Id, p.Name, p.Schedule, p.HandleMissedSync, p.DateNextRun))
                    .ToListAsync(cancellationToken);
            }

            var now = DateTimeOffset.Now;
            var registered = new List<(int Id, DateTimeOffset Next)>();
            var missed = new List<(int Id, string Name, DateTimeOffset Due)>();

            lock (_entriesLock)
            {
                _entries.Clear();
                foreach (var state in scheduled)
                {
                    if (TryParse(state.Schedule) is not { } cron || cron.GetNextOccurrence(now, _timeZone) is not { } next)
                    {
                        continue;
                    }

                    _entries[state.Id] = new ScheduledEntry(cron, next);
                    registered.Add((state.Id, next));

                    // Decide catch-up against the PREVIOUSLY persisted next-run (before we overwrite it).
                    if (ShouldCatchUp(state.HandleMissedSync, state.PersistedNextRun, now))
                    {
                        missed.Add((state.Id, state.Name, state.PersistedNextRun!.Value));
                    }
                }
            }

            // Record the fresh next-run for every scheduled profile.
            foreach (var (id, next) in registered)
            {
                await PersistNextRunAsync(id, next, cancellationToken);
            }

            // Run any profile that missed its scheduled time while the service was down, logging a visible
            // (profile-associated) operation log so the catch-up is distinguishable from a normal run.
            foreach (var (id, name, due) in missed)
            {
                logger.LogInformation("Profile {ProfileId} missed its scheduled run (due {Due}) while the service was down — running now.", id, due);
                await operationLogFactory.CreateAsync(
                    $"Missed scheduled run for '{name}' (was due {due.ToLocalTime():g}) — running now on startup",
                    profileId: id,
                    cancellationToken: cancellationToken);
                FireJob(id);
            }

            logger.LogInformation("Backup scheduler loaded {Count} scheduled profile(s).", _entries.Count);
        }

        /// <summary>
        /// Fires every entry whose next run has arrived (advancing it to its following occurrence), and
        /// returns how long to wait before the next evaluation plus the entries that advanced (so the caller
        /// can persist their new next-run time).
        /// </summary>
        private (TimeSpan Wait, List<(int Id, DateTimeOffset Next)> Advanced) FireDueAndComputeWait()
        {
            var now = DateTimeOffset.Now;
            var due = new List<int>();
            var advanced = new List<(int Id, DateTimeOffset Next)>();
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
                            advanced.Add((id, next.Value));
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
                return (MaxWait, advanced);
            }

            var wait = soonest.Value - DateTimeOffset.Now;
            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }
            return (wait < MaxWait ? wait : MaxWait, advanced);
        }

        /// <summary>Records a profile's next-run time (or clears it), tolerating a transient write failure.</summary>
        private async Task PersistNextRunAsync(int profileId, DateTimeOffset? nextRun, CancellationToken cancellationToken)
        {
            try
            {
                await using var db = contextFactory.CreateDbContext();
                await db.Profiles
                    .Where(p => p.Id == profileId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DateNextRun, nextRun), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to record next-run time for profile {ProfileId}.", profileId);
            }
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
                    await runner.RunAsync(profileId, manual: false, _stoppingToken);
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

        private sealed record ProfileScheduleState(int Id, string Name, string? Schedule, bool HandleMissedSync, DateTimeOffset? PersistedNextRun);
    }
}
