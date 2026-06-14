using BackupService.Database;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Authentication
{
    /// <summary>
    /// Default <see cref="IAuthenticationHistoryService"/>. Persists audit rows via the
    /// DbContext factory (a short-lived context per call), newest-first on read.
    /// </summary>
    public sealed class AuthenticationHistoryService(IDatabaseContextFactory contextFactory)
        : IAuthenticationHistoryService
    {
        public async Task RecordAsync(AuthenticationEventType eventType, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            db.AuthenticationHistory.Add(new AuthenticationHistory
            {
                EventType = eventType,
                TimestampUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<PagedResult<AuthenticationHistory>> GetPageAsync(
            int pageNumber, int pageSize, CancellationToken cancellationToken = default)
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

            var totalCount = await db.AuthenticationHistory.CountAsync(cancellationToken);

            // Order by Id (monotonic with insertion) for newest-first. SQLite cannot
            // ORDER BY a DateTimeOffset column, and Id order matches chronological order.
            var items = await db.AuthenticationHistory
                .AsNoTracking()
                .OrderByDescending(entry => entry.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return new PagedResult<AuthenticationHistory>(items, totalCount, pageNumber, pageSize);
        }
    }
}
