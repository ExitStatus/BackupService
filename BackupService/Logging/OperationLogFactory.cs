using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogFactory"/>. Persists via the DbContext factory (a
    /// short-lived context per call, per the project's factory convention).
    /// </summary>
    public sealed class OperationLogFactory(
        IDatabaseContextFactory contextFactory,
        ILogWatcher? logWatcher = null,
        ILogRetentionService? logRetentionService = null) : IOperationLogFactory
    {
        public async Task<IOperationLogger> CreateAsync(
            string name,
            OperationLogLevel level = OperationLogLevel.Info,
            int? profileId = null,
            CancellationToken cancellationToken = default)
        {
            var log = new OperationLog
            {
                Name = name,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = level,
                ProfileId = profileId,
            };

            await using (var db = contextFactory.CreateDbContext())
            {
                db.OperationLogs.Add(log);
                await db.SaveChangesAsync(cancellationToken);
            }

            // A new log header is a change worth surfacing to the Logs panel.
            logWatcher?.Notify();

            // Apply the retention policy after a log operation (at most once per day; fire-and-forget,
            // the service logs its own failures).
            _ = logRetentionService?.PurgeIfDueAsync();

            // EF populates log.Id after SaveChanges.
            return new OperationLogger(contextFactory, log.Id, level, logWatcher);
        }
    }
}
