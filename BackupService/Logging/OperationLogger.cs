using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogger"/>. Writes detail lines through the DbContext factory
    /// (a short-lived context per call, per the project convention), one row per message, tracking
    /// the next sequence number internally. Each line carries a level; the header's
    /// <see cref="OperationLog.Level"/> is kept in step as the most severe line seen — Debug/Info
    /// count as Info, any Warning escalates the header to Warning, any Error to Error. The header
    /// only ever rises (severity is never lowered). <see cref="SetSummaryAsync"/> revises the header
    /// message in place (its level acts as a floor).
    /// </summary>
    public sealed class OperationLogger(IDatabaseContextFactory contextFactory, int operationLogId, OperationLogLevel initialLevel, ILogWatcher? logWatcher = null)
        : IOperationLogger
    {
        private readonly object _levelGate = new();
        private int _sequence;
        private int _headerRank = HeaderRank(initialLevel);

        public int OperationLogId { get; } = operationLogId;

        public Task AppendAsync(params string[] messages) => WriteAsync(OperationLogLevel.Info, messages);

        public Task AppendAsync(OperationLogLevel level, params string[] messages) => WriteAsync(level, messages);

        public Task ErrorAsync(string message, Exception? exception = null)
        {
            if (exception is not null)
            {
                // Append the exception's message only — no stack trace (kept out of the operation log).
                message = $"{message}: {exception.Message}";
            }

            return WriteAsync(OperationLogLevel.Error, [message]);
        }

        public async Task SetSummaryAsync(string message, OperationLogLevel level)
        {
            await using var db = contextFactory.CreateDbContext();

            var log = await db.OperationLogs.FirstOrDefaultAsync(l => l.Id == OperationLogId);
            if (log is null)
            {
                return;
            }

            // The level is a floor — it can raise the header (e.g. force Error on a failed run) but
            // never lowers a level the detail lines already escalated to.
            TryRaiseHeader(level);
            log.Name = message;
            log.Level = CurrentHeaderLevel;
            await db.SaveChangesAsync();

            logWatcher?.Notify();
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
                    Level = level,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Sequence = Interlocked.Increment(ref _sequence),
                });
            }

            // Only touch the header row when this line actually raises its severity.
            if (TryRaiseHeader(level))
            {
                var log = await db.OperationLogs.FirstOrDefaultAsync(l => l.Id == OperationLogId);
                if (log is not null)
                {
                    log.Level = CurrentHeaderLevel;
                }
            }

            await db.SaveChangesAsync();

            logWatcher?.Notify();
        }

        private OperationLogLevel CurrentHeaderLevel => FromRank(Volatile.Read(ref _headerRank));

        /// <summary>Raises the tracked header rank to cover <paramref name="level"/>; true if it rose.</summary>
        private bool TryRaiseHeader(OperationLogLevel level)
        {
            var rank = HeaderRank(level);
            lock (_levelGate)
            {
                if (rank <= _headerRank)
                {
                    return false;
                }

                _headerRank = rank;
                return true;
            }
        }

        // Severity a line contributes to the header: Debug counts the same as Info (0).
        private static int HeaderRank(OperationLogLevel level) => level switch
        {
            OperationLogLevel.Error => 2,
            OperationLogLevel.Warning => 1,
            _ => 0, // Info or Debug
        };

        private static OperationLogLevel FromRank(int rank) => rank switch
        {
            2 => OperationLogLevel.Error,
            1 => OperationLogLevel.Warning,
            _ => OperationLogLevel.Info,
        };
    }
}
