using BackupService.Database;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Owns the step data within a scheduled task, kept separate from the generic task fields so a task
    /// save and its step changes stay one unit of work and one operation log. Mirrors the profile child
    /// services (e.g. <c>IArchiveSyncItemService</c>). Stateless: it operates on the tracked
    /// <see cref="ScheduledTask"/> graph.
    /// </summary>
    public interface IScheduledTaskStepService
    {
        /// <summary>Builds and adds new steps to a freshly created task.</summary>
        void Add(ScheduledTask task, IReadOnlyList<ScheduledTaskStepInput> inputs);

        /// <summary>
        /// Reconciles the task's steps with <paramref name="inputs"/> (updates matched ones by id, adds
        /// id-0 ones, removes the rest) and returns human-readable descriptions of the changes for the
        /// update log.
        /// </summary>
        IReadOnlyList<string> Sync(ScheduledTask task, IReadOnlyList<ScheduledTaskStepInput> inputs);

        /// <summary>The per-step detail lines describing the steps on a create log.</summary>
        IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<ScheduledTaskStepInput> inputs);
    }
}
