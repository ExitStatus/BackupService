namespace BackupService.Connections
{
    /// <summary>
    /// Loads a stored connection and decrypts its secret into runtime <see cref="SmbConnectionInfo"/>.
    /// Kept separate from <see cref="IConnectionService"/> so decryption stays off the grid read path;
    /// used by the Test button, the remote folder browser and the sync engine.
    /// </summary>
    public interface IConnectionResolver
    {
        Task<SmbConnectionInfo> GetSmbInfoAsync(int connectionId, CancellationToken cancellationToken = default);
    }
}
