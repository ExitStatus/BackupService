namespace BackupService.Connections
{
    /// <summary>
    /// Reports a connection's remaining storage for the Connections grid. Resolves a connection id to the
    /// right runtime info and dispatches to the matching connector by type. Best-effort: returns <c>null</c>
    /// when the figure can't be determined (the connection was unreachable).
    /// </summary>
    public interface IConnectionSpaceService
    {
        Task<StorageSpace?> GetSpaceAsync(int connectionId, CancellationToken cancellationToken = default);
    }
}
