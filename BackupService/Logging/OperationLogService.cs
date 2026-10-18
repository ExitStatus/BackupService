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
        public async Task<IReadOnlyDictionary<int, LastRunStatus>> GetLastRunStatusesAsync(
            IReadOnlyCollection<int> profileIds, CancellationToken cancellationToken = default)
        {
            if (profileIds.Count == 0)
            {
                return new Dictionary<int, LastRunStatus>();
            }

            await using var db = contextFactory.CreateDbContext();
            var ids = profileIds.ToList();

            // Newest run per profile by Id (monotonic with insertion — SQLite can't order a DateTimeOffset).
            var latestRunIds = await db.BackupRuns
                .Where(r => r.ProfileId != null && ids.Contains(r.ProfileId.Value))
                .GroupBy(r => r.ProfileId!.Value)
                .Select(g => g.Max(r => r.Id))
                .ToListAsync(cancellationToken);

            var runs = await db.BackupRuns
                .Where(r => latestRunIds.Contains(r.Id))
                .Select(r => new
                {
                    ProfileId = r.ProfileId!.Value,
                    r.Outcome,
                    Log = db.OperationLogs.Where(l => l.Id == r.OperationLogId).Select(l => new { l.Id, l.Level }).FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

            return runs.ToDictionary(
                r => r.ProfileId,
                r => r.Log is { } log ? new LastRunStatus(log.Level, log.Id) : new LastRunStatus(LevelFor(r.Outcome), null));
        }

        // The level a run's log ends at for its outcome — used when retention has already purged the log itself.
        private static OperationLogLevel LevelFor(RunOutcome outcome) => outcome switch
        {
            RunOutcome.Success => OperationLogLevel.Info,
            RunOutcome.CompletedWithWarnings => OperationLogLevel.Warning,
            _ => OperationLogLevel.Error,
        };

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

        public Task<OperationLogWindow> GetDetailWindowAsync(
            int operationLogId, int? skip, int take, CancellationToken cancellationToken = default) =>
            fileStore.ReadWindowAsync(operationLogId, skip, take, cancellationToken);

        public Task<OperationLogSearch> SearchDetailsAsync(
            int operationLogId, string? text, IReadOnlyCollection<OperationLogLevel>? levels, int maxMatches, CancellationToken cancellationToken = default) =>
            fileStore.SearchAsync(operationLogId, text, levels, maxMatches, cancellationToken);
    }
}
