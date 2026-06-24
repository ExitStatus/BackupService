namespace BackupService.Scheduling
{
    /// <summary>
    /// Keeps the running file watchers in sync with the LightroomArchive profiles in the database. Call
    /// <see cref="SyncAsync"/> after any change to a profile (create / update / enable / disable / delete)
    /// so the watchers are attached — or torn down — immediately. The LightroomArchive counterpart to
    /// <see cref="IInstantSyncManager"/>.
    /// </summary>
    public interface ILightroomArchiveManager
    {
        /// <summary>
        /// Re-reads the profile and (re)attaches a file watcher per item when it exists, is enabled, and is a
        /// <see cref="Enumerations.ProfileType.LightroomArchive"/> profile; otherwise removes any watchers it
        /// had. Safe to call for a just-deleted profile, or a profile of another type (a no-op then).
        /// </summary>
        Task SyncAsync(int profileId, CancellationToken cancellationToken = default);
    }
}
