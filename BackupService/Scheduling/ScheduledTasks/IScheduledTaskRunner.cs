namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// Runs a single pass of a scheduled task (its ordered steps). Invoked by the scheduler when the
    /// task's schedule fires, or on demand from the UI's "Run now" action. The scheduled-task counterpart
    /// of <c>IBackupRunner</c>.
    /// </summary>
    public interface IScheduledTaskRunner
    {
        /// <summary>
        /// Loads the task (with its steps), brackets the run with status/operation-log bookkeeping, runs
        /// the steps in order (stopping at the first non-zero exit), and records the run. No-op if the task
        /// no longer exists. When <paramref name="manual"/> is true (a "Run now" from the UI) the run's
        /// operation log is prefixed with <c>[Manual]</c>.
        /// </summary>
        Task RunAsync(int taskId, bool manual = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests that an in-progress run for the task stop (the grid's Stop button): the running step's
        /// process tree is killed, the operation log is finished as a <c>Warning</c> ("cancelled"), and the
        /// task returns to Idle. A no-op (returns false) when the task isn't currently running.
        /// </summary>
        bool RequestStop(int taskId);
    }
}
