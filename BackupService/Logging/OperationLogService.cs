using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogService"/>. Reads through the DbContext factory
    /// (a short-lived context per call), headers newest-first.
    /// </summary>
    public sealed class OperationLogService(IDatabaseContextFactory contextFactory) : IOperationLogService
    {
        public async Task<PagedResult<OperationLog>> GetPageAsync(
            int pageNumber,
            int pageSize,
            string? filter = null,
            bool includeMessages = false,
            OperationLogLevel? level = null,
            int? profileId = null,
            CancellationToken cancellationToken = default)
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

            var query = db.OperationLogs.AsNoTracking().Include(log => log.Profile).AsQueryable();

            if (level is not null)
            {
                query = query.Where(log => log.Level == level.Value);
            }

            if (profileId is not null)
            {
                query = query.Where(log => log.ProfileId == profileId.Value);
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                // LIKE is case-insensitive for ASCII in SQLite. Match the name, and (optionally)
                // any detail line's message.
                var pattern = $"%{filter.Trim()}%";
                query = includeMessages
                    ? query.Where(log =>
                        EF.Functions.Like(log.Name, pattern) ||
                        log.Details.Any(d => EF.Functions.Like(d.Message, pattern)))
                    : query.Where(log => EF.Functions.Like(log.Name, pattern));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            // Order by Id (monotonic with insertion) for newest-first. SQLite cannot
            // ORDER BY a DateTimeOffset column, and Id order matches chronological order.
            var items = await query
                .OrderByDescending(log => log.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            // Populate the (non-persisted) detail count for this page so the grid can hide the
            // expand control on detail-less logs — one grouped query for the page's ids.
            var ids = items.Select(item => item.Id).ToList();
            var counts = await db.OperationLogDetails
                .Where(detail => ids.Contains(detail.OperationLogId))
                .GroupBy(detail => detail.OperationLogId)
                .Select(group => new { group.Key, Count = group.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

            foreach (var item in items)
            {
                item.DetailCount = counts.GetValueOrDefault(item.Id);
            }

            return new PagedResult<OperationLog>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<IReadOnlyList<OperationLogDetail>> GetDetailsAsync(
            int operationLogId, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.OperationLogDetails
                .AsNoTracking()
                .Where(detail => detail.OperationLogId == operationLogId)
                .OrderBy(detail => detail.Sequence)
                .ToListAsync(cancellationToken);
        }
    }
}
