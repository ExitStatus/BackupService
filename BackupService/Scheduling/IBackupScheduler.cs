namespace BackupService.Scheduling
{
    /// <summary>
    /// Keeps the running schedule in sync with the profiles in the database. Call
    /// <see cref="SyncAsync"/> after any change to a profile (create / update / enable / disable /
    /// delete) so the scheduler picks it up — or drops it — immediately.
    /// </summary>
    public interface IBackupScheduler
    {
        /// <summary>
        /// Re-reads the profile and (re)schedules it when it exists, is enabled, and has a
        /// parseable cron schedule; otherwise removes it from the schedule. Safe to call for a
        /// profile that has just been deleted (the row is gone → it is unscheduled).
        /// </summary>
        Task SyncAsync(int profileId, CancellationToken cancellationToken = default);
    }
}
