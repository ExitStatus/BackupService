namespace BackupService.Connections
{
    /// <summary>
    /// Reports a connection's live status for the Connections grid (remaining storage, and whether it's currently
    /// contactable). Resolves a connection id to the right runtime info and dispatches to the matching connector by
    /// type. Best-effort: <see cref="GetSpaceAsync"/> returns <c>null</c> and <see cref="IsContactableAsync"/>
    /// returns <c>false</c> when the connection is unreachable.
    /// </summary>
    public interface IConnectionSpaceService
    {
        Task<StorageSpace?> GetSpaceAsync(int connectionId, CancellationToken cancellationToken = default);

        /// <summary>True when the connection can currently be reached (a live test round-trip), else false.</summary>
        Task<bool> IsContactableAsync(int connectionId, CancellationToken cancellationToken = default);
    }
}
