using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Security;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Connections
{
    /// <summary>
    /// Default <see cref="IConnectionService"/>. Persists via the DbContext factory (a short-lived
    /// context per call) and writes a profile-less operation log per mutation. The SMB password is
    /// encrypted at rest via <see cref="ISecretProtector"/> and never logged.
    /// </summary>
    public sealed class ConnectionService(
        IDatabaseContextFactory contextFactory,
        IOperationLogFactory operationLogFactory,
        ISecretProtector secretProtector) : IConnectionService
    {
        public async Task<int> CreateAsync(string name, ConnectionType type, SmbConnectionInput smb, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var connection = new Connection
            {
                Name = name,
                Type = type,
                DateCreated = DateTimeOffset.UtcNow,
                Smb = new SmbConnectionSettings
                {
                    Host = smb.Host,
                    Port = smb.Port,
                    ShareName = smb.Share,
                    Domain = NullIfBlank(smb.Domain),
                    Username = smb.Username,
                    PasswordEncrypted = secretProtector.Protect(smb.Password ?? string.Empty),
                    RootFolder = NullIfBlank(smb.RootFolder),
                },
            };

            db.Connections.Add(connection);
            await db.SaveChangesAsync(cancellationToken);

            var log = await operationLogFactory.CreateAsync($"Connection created: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync(DescribeSmb(name, type, smb).ToArray());

            return connection.Id;
        }

        public async Task UpdateAsync(int id, string name, SmbConnectionInput smb, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var connection = await db.Connections
                .Include(c => c.Smb)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (connection is null)
            {
                return;
            }

            var oldName = connection.Name;
            connection.Name = name;

            var settings = connection.Smb ??= new SmbConnectionSettings
            {
                ConnectionId = connection.Id,
                Host = smb.Host,
                ShareName = smb.Share,
                Username = smb.Username,
                PasswordEncrypted = string.Empty,
            };

            var oldSettings = (settings.Host, settings.Port, settings.ShareName, settings.Domain, settings.Username, settings.RootFolder);

            settings.Host = smb.Host;
            settings.Port = smb.Port;
            settings.ShareName = smb.Share;
            settings.Domain = NullIfBlank(smb.Domain);
            settings.Username = smb.Username;
            settings.RootFolder = NullIfBlank(smb.RootFolder);

            // A blank password on edit means "keep the stored one"; only re-encrypt when a new one is typed.
            var passwordChanged = !string.IsNullOrEmpty(smb.Password);
            if (passwordChanged)
            {
                settings.PasswordEncrypted = secretProtector.Protect(smb.Password!);
            }

            await db.SaveChangesAsync(cancellationToken);

            await LogUpdatedAsync(oldName, name, oldSettings, settings, passwordChanged, cancellationToken);
        }

        public async Task<PagedResult<Connection>> GetPageAsync(int pageNumber, int pageSize, ConnectionSortColumn sortColumn, bool descending, CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1)
            {
                pageNumber = 1;
            }
            if (pageSize < 1)
            {
                pageSize = 1;
            }

            await using var db = contextFactory.CreateDbContext();

            var query = db.Connections.AsNoTracking().Include(c => c.Smb);
            var totalCount = await query.CountAsync(cancellationToken);
            var skip = (pageNumber - 1) * pageSize;

            IReadOnlyList<Connection> items;
            if (sortColumn == ConnectionSortColumn.DateCreated)
            {
                // SQLite cannot ORDER BY a DateTimeOffset column, so sort it in memory (the list is small).
                var all = await query.ToListAsync(cancellationToken);
                var ordered = descending
                    ? all.OrderByDescending(c => c.DateCreated)
                    : all.OrderBy(c => c.DateCreated);
                items = ordered.Skip(skip).Take(pageSize).ToList();
            }
            else
            {
                IQueryable<Connection> ordered = sortColumn switch
                {
                    ConnectionSortColumn.Type => descending
                        ? query.OrderByDescending(c => c.Type)
                        : query.OrderBy(c => c.Type),
                    _ => descending
                        ? query.OrderByDescending(c => c.Name)
                        : query.OrderBy(c => c.Name),
                };
                items = await ordered.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
            }

            return new PagedResult<Connection>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Connection?> GetAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Connections
                .AsNoTracking()
                .Include(c => c.Smb)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        }

        public async Task<IReadOnlyList<ConnectionSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Connections
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new ConnectionSummary(c.Id, c.Name, c.Type))
                .ToListAsync(cancellationToken);
        }

        public async Task<ConnectionDeleteResult> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (connection is null)
            {
                return ConnectionDeleteResult.Success;
            }

            // Don't orphan any backup entry (folder pair, instant-sync or archive item) that references
            // this connection as a source/target.
            var inUse =
                await db.FolderPairs.CountAsync(fp => fp.SourceConnectionId == id || fp.TargetConnectionId == id, cancellationToken)
                + await db.InstantSyncItems.CountAsync(i => i.SourceConnectionId == id || i.TargetConnectionId == id, cancellationToken)
                + await db.ArchiveSyncItems.CountAsync(a => a.SourceConnectionId == id || a.TargetConnectionId == id, cancellationToken);
            if (inUse > 0)
            {
                return ConnectionDeleteResult.Blocked(
                    $"Connection is in use by {inUse} backup entr{(inUse == 1 ? "y" : "ies")}. Update or remove them first.");
            }

            var name = connection.Name;
            var type = connection.Type;

            db.Connections.Remove(connection);
            await db.SaveChangesAsync(cancellationToken);

            var log = await operationLogFactory.CreateAsync($"Connection deleted: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync($"Name: {name}", $"Type: {type.GetDescription()}");

            return ConnectionDeleteResult.Success;
        }

        private async Task LogUpdatedAsync(
            string oldName,
            string newName,
            (string Host, int Port, string ShareName, string? Domain, string Username, string? RootFolder) old,
            SmbConnectionSettings now,
            bool passwordChanged,
            CancellationToken cancellationToken)
        {
            var changes = new List<string>();

            if (oldName != newName)
            {
                changes.Add($"Name changed from '{oldName}' to '{newName}'");
            }
            if (old.Host != now.Host)
            {
                changes.Add($"Host changed from '{old.Host}' to '{now.Host}'");
            }
            if (old.Port != now.Port)
            {
                changes.Add($"Port changed from '{old.Port}' to '{now.Port}'");
            }
            if (old.ShareName != now.ShareName)
            {
                changes.Add($"Share changed from '{old.ShareName}' to '{now.ShareName}'");
            }
            if (old.Domain != now.Domain)
            {
                changes.Add($"Domain changed from '{DisplayText(old.Domain)}' to '{DisplayText(now.Domain)}'");
            }
            if (old.Username != now.Username)
            {
                changes.Add($"Username changed from '{old.Username}' to '{now.Username}'");
            }
            if (old.RootFolder != now.RootFolder)
            {
                changes.Add($"Root folder changed from '{DisplayText(old.RootFolder)}' to '{DisplayText(now.RootFolder)}'");
            }
            if (passwordChanged)
            {
                changes.Add("Password changed");
            }

            var log = await operationLogFactory.CreateAsync($"Connection updated: {oldName}", cancellationToken: cancellationToken);
            await log.AppendAsync(changes.Count == 0 ? ["No changes detected."] : changes.ToArray());
        }

        private static List<string> DescribeSmb(string name, ConnectionType type, SmbConnectionInput smb) =>
        [
            $"Name: {name}",
            $"Type: {type.GetDescription()}",
            $"Host: {smb.Host}",
            $"Port: {smb.Port}",
            $"Share: {smb.Share}",
            $"Domain: {DisplayText(smb.Domain)}",
            $"Username: {smb.Username}",
            $"Root folder: {DisplayText(smb.RootFolder)}",
        ];

        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static string DisplayText(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
