using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogFactory"/>. Persists via the DbContext factory (a
    /// short-lived context per call, per the project's factory convention).
    /// </summary>
    public sealed class OperationLogFactory(IDatabaseContextFactory contextFactory) : IOperationLogFactory
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

            // EF populates log.Id after SaveChanges.
            return new OperationLogger(contextFactory, log.Id, level);
        }
    }
}
