using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Application service for scheduled tasks (the scheduled-task counterpart of <c>IProfileService</c>).
    /// </summary>
    public interface IScheduledTaskService
    {
        /// <summary>Creates a new scheduled task with its ordered steps.</summary>
        Task CreateAsync(
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<ScheduledTaskStepInput> steps,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default);

        /// <summary>Reads a page of tasks ordered by the given column/direction.</summary>
        Task<PagedResult<ScheduledTask>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ScheduledTaskSortColumn sortColumn,
            bool descending,
            string? filter = null,
            bool? enabled = null,
            CancellationToken cancellationToken = default);

        /// <summary>Loads a single task (with its steps) for editing, or null if not found.</summary>
        Task<ScheduledTask?> GetAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>Deletes a task (and its steps + run history, via cascade). No-op if it doesn't exist.</summary>
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>Sets just the <see cref="ScheduledTask.Enabled"/> flag (used by the inline grid toggle).</summary>
        Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing task and syncs its steps (adds new ones, updates matched ones by id, removes
        /// the rest).
        /// </summary>
        Task UpdateAsync(
            int id,
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<ScheduledTaskStepInput> steps,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default);
    }
}
