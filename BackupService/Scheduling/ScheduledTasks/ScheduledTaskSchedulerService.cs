using BackupService.Database;
using BackupService.Logging;
using BackupService.ScheduledTasks;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// Background service that fires each enabled scheduled task on its cron schedule. Also the
    /// <see cref="IScheduledTaskScheduler"/> the rest of the app calls to keep the live schedule in sync
    /// with task changes. Registered once and shared in all three roles (singleton, scheduler API, hosted
    /// service) — the scheduled-task counterpart of <c>BackupSchedulerService</c>, reusing its pure cron
    /// helpers (<see cref="BackupSchedulerService.GetNextOccurrence"/>/<see cref="BackupSchedulerService.ShouldCatchUp"/>).
    /// </summary>
    public sealed class ScheduledTaskSchedulerService(
        IDatabaseContextFactory contextFactory,
        IScheduledTaskRunner runner,
        IScheduledTaskStatusService statusService,
        IOperationLogFactory operationLogFactory,
        ILogger<ScheduledTaskSchedulerService> logger) : BackgroundService, IScheduledTaskScheduler
    {
        // The longest the loop sleeps before re-evaluating, even when the next run is further off.
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

        private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
        private readonly Dictionary<int, ScheduledEntry> _entries = new();
        private readonly object _entriesLock = new();
        private readonly SemaphoreSlim _wake = new(0, 1);

        private CancellationToken _stoppingToken;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            logger.LogInformation("Scheduled-task scheduler started.");

            await LoadAllAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var (delay, advanced) = FireDueAndComputeWait();

                foreach (var (id, next) in advanced)
                {
                    await PersistNextRunAsync(id, next, stoppingToken);
                }

                try
                {
                    await _wake.WaitAsync(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Scheduled-task scheduler stopping.");
        }

        public async Task SyncAsync(int taskId, CancellationToken cancellationToken = default)
        {
            var info = await ReadTaskScheduleAsync(taskId, cancellationToken);

            DateTimeOffset? nextRun = null;
            lock (_entriesLock)
            {
                _entries.Remove(taskId);

                if (info is { Enabled: true } && TryParse(info.Schedule) is { } cron)
                {
                    var next = cron.GetNextOccurrence(DateTimeOffset.Now, _timeZone);
                    if (next.HasValue)
                    {
                        _entries[taskId] = new ScheduledEntry(cron, next.Value);
                        nextRun = next.Value;
                    }
                }
            }

            await PersistNextRunAsync(taskId, nextRun, cancellationToken);

            Wake();
        }

        /// <summary>Diagnostic/test helper: whether the task currently has a live schedule entry.</summary>
        public bool IsScheduled(int taskId)
        {
            lock (_entriesLock)
            {
                return _entries.ContainsKey(taskId);
            }
        }

        private async Task LoadAllAsync(CancellationToken cancellationToken)
        {
            List<TaskScheduleState> scheduled;
            await using (var db = contextFactory.CreateDbContext())
            {
                scheduled = await db.ScheduledTasks
                    .AsNoTracking()
                    .Where(t => t.Enabled && t.Schedule != null && t.Schedule != "")
                    .Select(t => new TaskScheduleState(t.Id, t.Name, t.Schedule, t.HandleMissedSync, t.DateNextRun))
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

                    if (BackupSchedulerService.ShouldCatchUp(state.HandleMissedSync, state.PersistedNextRun, now))
                    {
                        missed.Add((state.Id, state.Name, state.PersistedNextRun!.Value));
                    }
                }
            }

            foreach (var (id, next) in registered)
            {
                await PersistNextRunAsync(id, next, cancellationToken);
            }

            foreach (var (id, name, due) in missed)
            {
                logger.LogInformation("Scheduled task {TaskId} missed its scheduled run (due {Due}) while the service was down — running now.", id, due);
                await operationLogFactory.CreateAsync(
                    $"Missed scheduled run for task '{name}' (was due {due.ToLocalTime():g}) — running now on startup",
                    cancellationToken: cancellationToken);
                FireJob(id);
            }

            logger.LogInformation("Scheduled-task scheduler loaded {Count} scheduled task(s).", _entries.Count);
        }

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

        private async Task PersistNextRunAsync(int taskId, DateTimeOffset? nextRun, CancellationToken cancellationToken)
        {
            try
            {
                await using var db = contextFactory.CreateDbContext();
                await db.ScheduledTasks
                    .Where(t => t.Id == taskId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.DateNextRun, nextRun), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to record next-run time for task {TaskId}.", taskId);
            }
        }

        private void FireJob(int taskId)
        {
            if (statusService.IsRunning(taskId))
            {
                logger.LogInformation("Skipping scheduled run for task {TaskId}: a run is already in progress.", taskId);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(taskId, manual: false, _stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error running scheduled task {TaskId}.", taskId);
                }
            });
        }

        private async Task<TaskScheduleInfo?> ReadTaskScheduleAsync(int taskId, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.ScheduledTasks
                .AsNoTracking()
                .Where(t => t.Id == taskId)
                .Select(t => new TaskScheduleInfo(t.Enabled, t.Schedule))
                .FirstOrDefaultAsync(cancellationToken);
        }

        private void Wake()
        {
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

        private sealed record TaskScheduleInfo(bool Enabled, string? Schedule);

        private sealed record TaskScheduleState(int Id, string Name, string? Schedule, bool HandleMissedSync, DateTimeOffset? PersistedNextRun);
    }
}
