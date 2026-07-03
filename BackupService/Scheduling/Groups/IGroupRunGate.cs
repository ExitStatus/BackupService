namespace BackupService.Scheduling.Groups
{
    /// <summary>
    /// Serialises runs of profiles that share a <b>Sequential</b> group so only one runs at a time.
    /// A Parallel group (or an ungrouped profile) passes a null id and acquires nothing.
    /// </summary>
    public interface IGroupRunGate
    {
        /// <summary>
        /// Waits until no other run holds this Sequential group's gate, then takes it for this run. Pass
        /// <paramref name="sequentialGroupId"/> null (Parallel group / ungrouped) for a no-op releaser.
        /// Dispose the returned handle to release.
        /// </summary>
        Task<IAsyncDisposable> AcquireAsync(int? sequentialGroupId, CancellationToken cancellationToken);
    }
}
