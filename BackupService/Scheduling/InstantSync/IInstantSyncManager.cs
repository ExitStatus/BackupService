namespace BackupService.Scheduling
{
    /// <summary>
    /// Keeps the running file watchers in sync with the InstantSync profiles in the database. Call
    /// <see cref="SyncAsync"/> after any change to a profile (create / update / enable / disable /
    /// delete) so the watchers are attached — or torn down — immediately. The instant-sync counterpart
    /// to <see cref="IBackupScheduler"/>.
    /// </summary>
    public interface IInstantSyncManager
    {
        /// <summary>
        /// Re-reads the profile and (re)attaches a file watcher per item when it exists, is enabled,
        /// and is an <see cref="Enumerations.ProfileType.InstantSync"/> profile; otherwise removes any
        /// watchers it had. Safe to call for a profile that has just been deleted, or for a profile of
        /// another type (it is a no-op then).
        /// </summary>
        Task SyncAsync(int profileId, CancellationToken cancellationToken = default);
    }
}
