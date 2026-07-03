using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogService"/>. Reads headers through the DbContext factory
    /// (a short-lived context per call), newest-first, and reads detail lines from each log's
    /// on-disk file via <see cref="IOperationLogFileStore"/>.
    /// </summary>
    public sealed class OperationLogService(
        IDatabaseContextFactory contextFactory,
        IOperationLogFileStore fileStore) : IOperationLogService
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

            var trimmedFilter = filter?.Trim();
            var hasFilter = !string.IsNullOrEmpty(trimmedFilter);

            // Message search can't be done in SQL any more (lines live in files), so when it's requested
            // we evaluate the whole (level/profile-filtered) set in memory: match on the name, or by
            // scanning each log's file. Ordering is by Id (monotonic with insertion) for newest-first —
            // SQLite cannot ORDER BY a DateTimeOffset column, and Id order matches chronological order.
            if (hasFilter && includeMessages)
            {
                var all = await query.OrderByDescending(log => log.Id).ToListAsync(cancellationToken);

                var matched = new List<OperationLog>(all.Count);
                foreach (var log in all)
                {
                    if (log.Name.Contains(trimmedFilter!, StringComparison.OrdinalIgnoreCase)
                        || (log.LogFile is not null && await fileStore.ContainsAsync(log.Id, trimmedFilter!, cancellationToken)))
                    {
                        matched.Add(log);
                    }
                }

                var page = matched
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return new PagedResult<OperationLog>(page, matched.Count, pageNumber, pageSize);
            }

            if (hasFilter)
            {
                // LIKE is case-insensitive for ASCII in SQLite.
                var pattern = $"%{trimmedFilter}%";
                query = query.Where(log => EF.Functions.Like(log.Name, pattern));
            }

            var totalCount = await query.CountAsync(cancellationToken);

            var items = await query
                .OrderByDescending(log => log.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return new PagedResult<OperationLog>(items, totalCount, pageNumber, pageSize);
        }

        public Task<IReadOnlyList<OperationLogLine>> GetDetailsAsync(
            int operationLogId, CancellationToken cancellationToken = default) =>
            fileStore.ReadAsync(operationLogId, cancellationToken);

        public Task<IReadOnlyList<OperationLogLine>> GetRecentDetailsAsync(
            int operationLogId, int maxLines, CancellationToken cancellationToken = default) =>
            fileStore.ReadTailAsync(operationLogId, maxLines, cancellationToken);
    }
}
