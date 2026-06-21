using BackupService.Database;
using BackupService.Security;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Connections
{
    /// <summary>
    /// Default <see cref="IConnectionResolver"/>: reads the connection's SMB settings and decrypts the
    /// password via <see cref="ISecretProtector"/>.
    /// </summary>
    public sealed class ConnectionResolver(
        IDatabaseContextFactory contextFactory,
        ISecretProtector secretProtector) : IConnectionResolver
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
    }
}
