using BackupService.Database;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="ILogRetentionService"/>. Reads/writes the single
    /// <see cref="LogRetentionSettings"/> row and purges old log rows via the DbContext factory.
    /// <para>SQLite cannot compare a <see cref="DateTimeOffset"/> column (the existing log services
    /// order by <c>Id</c> for the same reason), so the purge projects <c>(Id, TimestampUtc)</c> into
    /// memory, finds the newest row older than the cutoff, and deletes by <c>Id</c> range — rows are
    /// inserted in time order, so <c>Id</c> is monotonic with their timestamp.</para>
    /// </summary>
    public sealed class LogRetentionService(
        IDatabaseContextFactory contextFactory,
        TimeProvider timeProvider,
        ILogger<LogRetentionService> logger) : ILogRetentionService
    {
        private readonly object _gate = new();
        private DateOnly? _lastPurgedUtcDate;
        private bool _purging;

        public async Task<LogRetentionSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();
            return await GetOrSeedAsync(db, cancellationToken);
        }

        public async Task UpdateSettingsAsync(int authenticationLogRetentionDays, int operationLogRetentionDays, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await GetOrSeedAsync(db, cancellationToken);
            settings.AuthenticationLogRetentionDays = Math.Max(1, authenticationLogRetentionDays);
            settings.OperationLogRetentionDays = Math.Max(1, operationLogRetentionDays);
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task PurgeIfDueAsync(CancellationToken cancellationToken = default)
        {
            var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

            lock (_gate)
            {
                // Already purged today, or a purge is in flight — nothing to do.
                if (_purging || _lastPurgedUtcDate == today)
                {
                    return;
                }
                _purging = true;
            }

            try
            {
                await PurgeAsync(cancellationToken);

                // Mark done only on success, so a failed purge retries on the next log operation.
                lock (_gate)
                {
                    _lastPurgedUtcDate = today;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Log retention purge failed; it will be retried on the next log operation.");
            }
            finally
            {
                lock (_gate)
                {
                    _purging = false;
                }
            }
        }

        private async Task PurgeAsync(CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await GetOrSeedAsync(db, cancellationToken);
            var now = timeProvider.GetUtcNow();

            await PurgeAuthenticationAsync(db, now.AddDays(-settings.AuthenticationLogRetentionDays), cancellationToken);
            await PurgeOperationAsync(db, now.AddDays(-settings.OperationLogRetentionDays), cancellationToken);
        }

        private async Task PurgeAuthenticationAsync(BackupDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            var boundaryId = await OldestBoundaryIdAsync(
                db.AuthenticationHistory.AsNoTracking().Select(h => new IdTimestamp(h.Id, h.TimestampUtc)),
                cutoff,
                cancellationToken);

            if (boundaryId is not int id)
            {
                return;
            }

            var deleted = await db.AuthenticationHistory
                .Where(h => h.Id <= id)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation("Purged {Count} authentication history row(s) older than {Cutoff:u}.", deleted, cutoff);
            }
        }

        private async Task PurgeOperationAsync(BackupDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            var boundaryId = await OldestBoundaryIdAsync(
                db.OperationLogs.AsNoTracking().Select(l => new IdTimestamp(l.Id, l.TimestampUtc)),
                cutoff,
                cancellationToken);

            if (boundaryId is not int id)
            {
                return;
            }

            // Delete detail lines first, then the headers, so we don't rely on SQLite FK cascade under
            // ExecuteDelete (which issues a single raw DELETE).
            await db.OperationLogDetails
                .Where(d => d.OperationLogId <= id)
                .ExecuteDeleteAsync(cancellationToken);

            var deleted = await db.OperationLogs
                .Where(l => l.Id <= id)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogInformation("Purged {Count} operation log(s) older than {Cutoff:u}.", deleted, cutoff);
            }
        }

        /// <summary>
        /// Largest <c>Id</c> whose timestamp is strictly before <paramref name="cutoff"/>, or null if
        /// none. Evaluated in memory because SQLite can't compare a <see cref="DateTimeOffset"/> column.
        /// </summary>
        private static async Task<int?> OldestBoundaryIdAsync(IQueryable<IdTimestamp> rows, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            var all = await rows.ToListAsync(cancellationToken);
            return all
                .Where(r => r.TimestampUtc < cutoff)
                .Select(r => (int?)r.Id)
                .DefaultIfEmpty(null)
                .Max();
        }

        private static async Task<LogRetentionSettings> GetOrSeedAsync(BackupDbContext db, CancellationToken cancellationToken)
        {
            var settings = await db.LogRetentionSettings.FirstOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = new LogRetentionSettings();
                db.LogRetentionSettings.Add(settings);
                await db.SaveChangesAsync(cancellationToken);
            }

            return settings;
        }

        private readonly record struct IdTimestamp(int Id, DateTimeOffset TimestampUtc);
    }
}
