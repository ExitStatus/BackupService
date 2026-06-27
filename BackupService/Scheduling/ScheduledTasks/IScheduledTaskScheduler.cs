namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// Keeps the running schedule in sync with the scheduled tasks in the database. Call
    /// <see cref="SyncAsync"/> after any change to a task (create / update / enable / disable / delete)
    /// so the scheduler picks it up — or drops it — immediately. The scheduled-task counterpart of
    /// <c>IBackupScheduler</c>.
    /// </summary>
    public interface IScheduledTaskScheduler
    {
        /// <summary>
        /// Re-reads the task and (re)schedules it when it exists, is enabled, and has a parseable cron
        /// schedule; otherwise removes it from the schedule. Safe to call for a task that has just been
        /// deleted (the row is gone → it is unscheduled).
        /// </summary>
        Task SyncAsync(int taskId, CancellationToken cancellationToken = default);
    }
}
