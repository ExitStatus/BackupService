namespace BackupService.Scheduling
{
    /// <summary>
    /// Runs a single backup pass for a profile. Invoked by the scheduler when a profile's
    /// schedule fires, or on demand from the UI's "Run now" action; dispatches to the
    /// <see cref="IProfileTypeHandler"/> matching the profile's type.
    /// </summary>
    public interface IBackupRunner
    {
        /// <summary>
        /// Loads the profile (with its folder pairs), records start/finish operation logs and
        /// status, and dispatches to the handler for the profile's type. No-op if the profile
        /// no longer exists. When <paramref name="manual"/> is true (a "Run now" from the UI rather
        /// than the schedule) the run's operation log is prefixed with <c>[Manual]</c>.
        /// </summary>
        Task RunAsync(int profileId, bool manual = false, CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests that an in-progress run for the profile stop (the grid's Stop button). The run
        /// unwinds cooperatively — any crash-safe temp file is removed, so nothing partial is left
        /// behind — its operation log is finished as a <c>Warning</c> ("cancelled"), and the profile
        /// returns to Idle to await its next scheduled run. A no-op (returns false) when the profile
        /// isn't currently running.
        /// </summary>
        bool RequestStop(int profileId);
    }
}
