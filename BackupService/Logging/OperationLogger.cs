using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogger"/>. Detail lines are appended to the log's on-disk file via
    /// <see cref="IOperationLogFileStore"/> (a cheap file append per line, not a database insert). The
    /// database header row (<see cref="OperationLog"/>) is touched only when it needs to change: once on
    /// the first write to record the <see cref="OperationLog.LogFile"/> reference, and thereafter only when
    /// a line actually raises the header's <see cref="OperationLog.Level"/> (Debug/Info count as Info, any
    /// Warning escalates to Warning, any Error to Error — the header only ever rises).
    /// <see cref="SetSummaryAsync"/> revises the header message in place (its level acts as a floor).
    /// </summary>
    public sealed class OperationLogger(
        IDatabaseContextFactory contextFactory,
        IOperationLogFileStore fileStore,
        int operationLogId,
        OperationLogLevel initialLevel,
        ILogWatcher? logWatcher = null)
        : IOperationLogger
    {
        private readonly object _levelGate = new();
        private int _headerRank = HeaderRank(initialLevel);
        private int _fileStarted; // 0 until the first line is written (then the header records LogFile once).

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

            // The line(s) go to the file — the write-heavy part, and no database round-trip.
            await fileStore.AppendAsync(OperationLogId, level, messages);

            // Touch the header only when something about it changed: the first write records the LogFile
            // reference, and any write that escalated the severity updates the level.
            var firstWrite = Interlocked.Exchange(ref _fileStarted, 1) == 0;
            var raised = TryRaiseHeader(level);
            if (firstWrite || raised)
            {
                await using var db = contextFactory.CreateDbContext();
                var log = await db.OperationLogs.FirstOrDefaultAsync(l => l.Id == OperationLogId);
                if (log is not null)
                {
                    if (firstWrite)
                    {
                        log.LogFile = fileStore.FileNameFor(OperationLogId);
                    }
                    if (raised)
                    {
                        log.Level = CurrentHeaderLevel;
                    }
                    await db.SaveChangesAsync();
                }
            }

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
