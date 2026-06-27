using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Scheduling;
using BackupService.Scheduling.ScheduledTasks;
using Microsoft.EntityFrameworkCore;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Default <see cref="IScheduledTaskService"/>. Persists via the DbContext factory (a short-lived
    /// context per call), writes a profile-less operation log per mutation, keeps the in-memory status
    /// tracker in step, and re-syncs the scheduler after every change. Mirrors <c>ProfileService</c>.
    /// </summary>
    public sealed class ScheduledTaskService(
        IDatabaseContextFactory contextFactory,
        IOperationLogFactory operationLogFactory,
        IScheduledTaskStepService stepService,
        IScheduledTaskScheduler scheduler,
        IScheduledTaskStatusService statusService) : IScheduledTaskService
    {
        public async Task CreateAsync(
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<ScheduledTaskStepInput> steps,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var task = new ScheduledTask
            {
                Name = name,
                Description = description,
                Schedule = scheduleCron,
                Enabled = enabled,
                HandleMissedSync = handleMissedSync,
                DateCreated = DateTimeOffset.UtcNow,
            };

            stepService.Add(task, steps);

            db.ScheduledTasks.Add(task);
            await db.SaveChangesAsync(cancellationToken);

            await LogCreatedAsync(name, description, scheduleCron, enabled, handleMissedSync, steps, cancellationToken);

            statusService.Set(task.Id, ProfileStatus.Idle);
            await scheduler.SyncAsync(task.Id, cancellationToken);
        }

        public async Task<PagedResult<ScheduledTask>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ScheduledTaskSortColumn sortColumn,
            bool descending,
            string? filter = null,
            bool? enabled = null,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1)
            {
                pageNumber = 1;
            }
            if (pageSize < 1)
            {
                pageSize = 1;
            }

            await using var db = contextFactory.CreateDbContext();

            var query = db.ScheduledTasks.AsNoTracking().Include(t => t.Steps).AsQueryable();
            if (enabled is { } en)
            {
                query = query.Where(t => t.Enabled == en);
            }
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var like = $"%{filter.Trim()}%";
                query = query.Where(t => EF.Functions.Like(t.Name, like)
                    || (t.Description != null && EF.Functions.Like(t.Description, like)));
            }
            var totalCount = await query.CountAsync(cancellationToken);
            var skip = (pageNumber - 1) * pageSize;

            IReadOnlyList<ScheduledTask> items;
            if (sortColumn is ScheduledTaskSortColumn.DateLastRun or ScheduledTaskSortColumn.DateNextRun)
            {
                // SQLite cannot ORDER BY a DateTimeOffset column, so sort these in memory. The task count
                // is small (an admin-managed local list).
                var all = await query.ToListAsync(cancellationToken);
                Func<ScheduledTask, DateTimeOffset?> key = sortColumn == ScheduledTaskSortColumn.DateLastRun
                    ? t => t.DateLastRun
                    : t => t.DateNextRun;
                var ordered = descending ? all.OrderByDescending(key) : all.OrderBy(key);
                items = ordered.Skip(skip).Take(pageSize).ToList();
            }
            else
            {
                var ordered = descending ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name);
                items = await ordered.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
            }

            return new PagedResult<ScheduledTask>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<ScheduledTask?> GetAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.ScheduledTasks
                .AsNoTracking()
                .Include(t => t.Steps)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (task is null)
            {
                return;
            }

            var name = task.Name;
            var description = task.Description;

            db.ScheduledTasks.Remove(task);
            await db.SaveChangesAsync(cancellationToken);

            // Not associated with anything — the deletion log is meant to survive.
            var log = await operationLogFactory.CreateAsync($"Scheduled task deleted: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync(
                $"Name: {name}",
                $"Description: {DisplayText(description)}");

            statusService.Remove(id);
            await scheduler.SyncAsync(id, cancellationToken);
        }

        public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (task is null)
            {
                return;
            }

            task.Enabled = enabled;
            await db.SaveChangesAsync(cancellationToken);

            await operationLogFactory.CreateAsync(
                $"Scheduled task {task.Name} was {(enabled ? "enabled" : "disabled")}",
                cancellationToken: cancellationToken);

            await scheduler.SyncAsync(id, cancellationToken);
        }

        public async Task UpdateAsync(
            int id,
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<ScheduledTaskStepInput> steps,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var task = await db.ScheduledTasks
                .Include(t => t.Steps)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (task is null)
            {
                return;
            }

            var oldName = task.Name;
            var oldDescription = task.Description;
            var oldSchedule = task.Schedule;
            var oldEnabled = task.Enabled;
            var oldHandleMissedSync = task.HandleMissedSync;

            task.Name = name;
            task.Description = description;
            task.Schedule = scheduleCron;
            task.Enabled = enabled;
            task.HandleMissedSync = handleMissedSync;

            var stepChanges = stepService.Sync(task, steps);

            await db.SaveChangesAsync(cancellationToken);

            await LogUpdatedAsync(
                oldName, name, oldDescription, description, oldSchedule, scheduleCron,
                oldEnabled, enabled, oldHandleMissedSync, handleMissedSync, stepChanges, cancellationToken);

            await scheduler.SyncAsync(id, cancellationToken);
        }

        private async Task LogCreatedAsync(
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            bool handleMissedSync,
            IReadOnlyList<ScheduledTaskStepInput> steps,
            CancellationToken cancellationToken)
        {
            var log = await operationLogFactory.CreateAsync($"Scheduled task created: {name}", cancellationToken: cancellationToken);

            await log.AppendAsync(
                $"Name: {name}",
                $"Description: {DisplayText(description)}",
                $"Schedule: {ScheduleDefinition.Describe(scheduleCron)}",
                $"Handle missed sync: {YesNo(handleMissedSync)}",
                $"Enabled: {YesNo(enabled)}");

            var stepLines = stepService.DescribeForCreateLog(steps);
            if (stepLines.Count > 0)
            {
                await log.AppendAsync(stepLines.ToArray());
            }
        }

        private async Task LogUpdatedAsync(
            string oldName,
            string newName,
            string? oldDescription,
            string? newDescription,
            string? oldSchedule,
            string? newSchedule,
            bool oldEnabled,
            bool newEnabled,
            bool oldHandleMissedSync,
            bool newHandleMissedSync,
            IReadOnlyList<string> stepChanges,
            CancellationToken cancellationToken)
        {
            var changes = new List<string>();

            if (oldName != newName)
            {
                changes.Add($"Name changed from '{oldName}' to '{newName}'");
            }
            if (oldDescription != newDescription)
            {
                changes.Add($"Description changed from '{DisplayText(oldDescription)}' to '{DisplayText(newDescription)}'");
            }
            if (oldSchedule != newSchedule)
            {
                changes.Add($"Schedule changed from '{ScheduleDefinition.Describe(oldSchedule)}' to '{ScheduleDefinition.Describe(newSchedule)}'");
            }
            if (oldEnabled != newEnabled)
            {
                changes.Add($"Enabled changed from '{YesNo(oldEnabled)}' to '{YesNo(newEnabled)}'");
            }
            if (oldHandleMissedSync != newHandleMissedSync)
            {
                changes.Add($"Handle missed sync changed from '{YesNo(oldHandleMissedSync)}' to '{YesNo(newHandleMissedSync)}'");
            }

            changes.AddRange(stepChanges);

            var log = await operationLogFactory.CreateAsync($"Scheduled task updated: {oldName}", cancellationToken: cancellationToken);

            if (changes.Count == 0)
            {
                await log.AppendAsync("No changes detected.");
                return;
            }

            await log.AppendAsync(changes.ToArray());
        }

        private static string DisplayText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(none)" : value;

        private static string YesNo(bool value) => value ? "Yes" : "No";
    }
}
