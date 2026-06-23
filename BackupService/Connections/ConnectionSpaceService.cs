using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Smb;
using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// Default <see cref="IConnectionSpaceService"/>: branches on the connection's type (via
    /// <see cref="IConnectionResolver.GetTypeAsync"/>), decrypts the matching info, and asks that connector
    /// for its free space. Any failure (unreachable, bad credentials) yields <c>null</c> rather than throwing.
    /// </summary>
    public sealed class ConnectionSpaceService(
        IConnectionResolver resolver,
        ISmbConnector smbConnector,
        IGoogleDriveConnector googleDriveConnector,
        ILogger<ConnectionSpaceService> logger) : IConnectionSpaceService
    {
        public async Task<StorageSpace?> GetSpaceAsync(int connectionId, CancellationToken cancellationToken = default)
        {
            try
            {
                switch (await resolver.GetTypeAsync(connectionId, cancellationToken))
                {
                    case ConnectionType.GoogleDrive:
                        var googleDrive = await resolver.GetGoogleDriveInfoAsync(connectionId, cancellationToken);
                        return await googleDriveConnector.GetFreeSpaceAsync(googleDrive, cancellationToken);

                    case ConnectionType.Smb:
                        var smb = await resolver.GetSmbInfoAsync(connectionId, cancellationToken);
                        return await smbConnector.GetFreeSpaceAsync(smb, cancellationToken);

                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not determine free space for connection {ConnectionId}.", connectionId);
                return null;
            }
        }
    }
}
