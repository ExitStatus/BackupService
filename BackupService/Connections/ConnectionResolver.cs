using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Usb;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Security;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Connections
{
    /// <summary>
    /// Default <see cref="IConnectionResolver"/>: reads a connection's settings and decrypts its secrets via
    /// <see cref="ISecretProtector"/>.
    /// </summary>
    public sealed class ConnectionResolver(
        IDatabaseContextFactory contextFactory,
        ISecretProtector secretProtector,
        GoogleDriveAppCredentials googleDriveAppCredentials) : IConnectionResolver
    {
        public async Task<SmbConnectionInfo> GetSmbInfoAsync(int connectionId, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await db.SmbConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ConnectionId == connectionId, cancellationToken)
                ?? throw new InvalidOperationException($"Connection {connectionId} has no SMB settings.");

            var password = string.IsNullOrEmpty(settings.PasswordEncrypted)
                ? string.Empty
                : secretProtector.Unprotect(settings.PasswordEncrypted);

            return new SmbConnectionInfo(
                settings.Host,
                settings.Port,
                settings.ShareName,
                settings.Domain,
                settings.Username,
                password,
                settings.RootFolder);
        }

        public async Task<GoogleDriveConnectionInfo> GetGoogleDriveInfoAsync(int connectionId, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await db.GoogleDriveConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ConnectionId == connectionId, cancellationToken)
                ?? throw new InvalidOperationException($"Connection {connectionId} has no Google Drive settings.");

            var refreshToken = string.IsNullOrEmpty(settings.RefreshTokenEncrypted)
                ? string.Empty
                : secretProtector.Unprotect(settings.RefreshTokenEncrypted);

            // A built-in-client connection refreshes against the app's configured client; a custom one uses
            // its own stored id/secret.
            var clientId = settings.UsesBuiltInClient ? googleDriveAppCredentials.ClientId ?? string.Empty : settings.ClientId;
            var clientSecret = settings.UsesBuiltInClient
                ? googleDriveAppCredentials.ClientSecret ?? string.Empty
                : string.IsNullOrEmpty(settings.ClientSecretEncrypted) ? string.Empty : secretProtector.Unprotect(settings.ClientSecretEncrypted);

            return new GoogleDriveConnectionInfo(
                clientId,
                clientSecret,
                refreshToken,
                settings.AccountEmail,
                settings.RootFolder);
        }

        public async Task<UsbConnectionInfo> GetUsbInfoAsync(int connectionId, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await db.UsbConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ConnectionId == connectionId, cancellationToken)
                ?? throw new InvalidOperationException($"Connection {connectionId} has no USB settings.");

            // No secret to decrypt — USB identity is plain.
            return new UsbConnectionInfo(settings.Kind, settings.HardwareSerial, settings.VolumeSerial, settings.MtpSerial, settings.RootFolder);
        }

        public async Task<ConnectionType> GetTypeAsync(int connectionId, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Connections
                .AsNoTracking()
                .Where(c => c.Id == connectionId)
                .Select(c => c.Type)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
