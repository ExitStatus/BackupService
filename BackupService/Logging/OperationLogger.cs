using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogger"/>. Writes detail lines through the DbContext factory
    /// (a short-lived context per call, per the project convention), one row per message, tracking
    /// the next sequence number internally.
    /// </summary>
    public sealed class OperationLogger(IDatabaseContextFactory contextFactory, int operationLogId) : IOperationLogger
    {
        private int _sequence;

        public int OperationLogId { get; } = operationLogId;

        public Task InfoAsync(params string[] messages) => WriteAsync(OperationLogLevel.Info, messages);

        public Task WarningAsync(params string[] messages) => WriteAsync(OperationLogLevel.Warning, messages);

        public Task DebugAsync(params string[] messages) => WriteAsync(OperationLogLevel.Debug, messages);

        public Task ErrorAsync(string message, Exception? exception = null)
        {
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            return WriteAsync(OperationLogLevel.Error, [message]);
        }

        private async Task WriteAsync(OperationLogLevel level, string[] messages)
        {
            if (messages.Length == 0)
            {
                return;
            }

            await using var db = contextFactory.CreateDbContext();

            foreach (var message in messages)
            {
                db.OperationLogDetails.Add(new OperationLogDetail
                {
                    OperationLogId = OperationLogId,
                    Message = message,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Level = level,
                    Sequence = Interlocked.Increment(ref _sequence),
                });
            }

            await db.SaveChangesAsync();
        }
    }
}
