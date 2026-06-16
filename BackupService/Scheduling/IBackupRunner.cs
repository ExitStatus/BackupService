namespace BackupService.Scheduling
{
    /// <summary>
    /// Runs a single backup pass for a profile. Invoked by the scheduler when a profile's
    /// schedule fires; dispatches to the <see cref="IProfileTypeHandler"/> matching the
    /// profile's type.
    /// </summary>
    public interface IBackupRunner
    {
        /// <summary>
        /// Loads the profile (with its folder pairs), records start/finish operation logs and
        /// status, and dispatches to the handler for the profile's type. No-op if the profile
        /// no longer exists.
        /// </summary>
        Task RunAsync(int profileId, CancellationToken cancellationToken = default);
    }
}
