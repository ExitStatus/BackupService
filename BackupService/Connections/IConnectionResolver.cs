using BackupService.Connections.GoogleDrive;
using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// Loads a stored connection and decrypts its secrets into runtime info (e.g.
    /// <see cref="SmbConnectionInfo"/> / <see cref="GoogleDriveConnectionInfo"/>). Kept separate from
    /// <see cref="IConnectionService"/> so decryption stays off the grid read path; used by the Test button,
    /// the remote folder browser and the sync engine.
    /// </summary>
    public interface IConnectionResolver
    {
        Task<SmbConnectionInfo> GetSmbInfoAsync(int connectionId, CancellationToken cancellationToken = default);

        Task<GoogleDriveConnectionInfo> GetGoogleDriveInfoAsync(int connectionId, CancellationToken cancellationToken = default);

        /// <summary>The connection's type — lets callers branch to the right info/browser without loading settings.</summary>
        Task<ConnectionType> GetTypeAsync(int connectionId, CancellationToken cancellationToken = default);
    }
}
