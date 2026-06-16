using BackupService.Database;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogger"/>. Writes detail lines through the DbContext factory
    /// (a short-lived context per call, per the project convention), one row per message, tracking
    /// the next sequence number internally. Severity is fixed on the log header at creation time.
    /// </summary>
    public sealed class OperationLogger(IDatabaseContextFactory contextFactory, int operationLogId) : IOperationLogger
    {
        private int _sequence;

        public int OperationLogId { get; } = operationLogId;

        public Task AppendAsync(params string[] messages) => WriteAsync(messages);

        public Task ErrorAsync(string message, Exception? exception = null)
        {
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }

            return WriteAsync([message]);
        }

        private async Task WriteAsync(string[] messages)
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
                    Sequence = Interlocked.Increment(ref _sequence),
                });
            }

            await db.SaveChangesAsync();
        }
    }
}
