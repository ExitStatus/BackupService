using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Stores an operation log's detail lines in a single per-log text file (one file per
    /// <see cref="Database.OperationLog"/>, named by its id) rather than as individual database rows.
    /// Each line is written with an <c>[Level][yyyy-MM-dd HH:mm:ss] message</c> prefix so the files are
    /// human-readable and re-parseable; a message that itself contains newlines is written across several
    /// physical lines (the continuation lines have no prefix) and reconstructed on read.
    /// </summary>
    public interface IOperationLogFileStore
    {
        /// <summary>The file name (not full path) recorded in <see cref="Database.OperationLog.LogFile"/>.</summary>
        string FileNameFor(int operationLogId);

        /// <summary>Appends one formatted line per message, stamped with the current local time.</summary>
        Task AppendAsync(int operationLogId, OperationLogLevel level, IReadOnlyList<string> messages, CancellationToken cancellationToken = default);

        /// <summary>Writes a whole log file from pre-existing lines (used by the one-time DB→file migration).</summary>
        Task WriteAllAsync(int operationLogId, IReadOnlyList<OperationLogLine> lines, CancellationToken cancellationToken = default);

        /// <summary>Reads and parses a log's file into its lines (empty if the file does not exist).</summary>
        Task<IReadOnlyList<OperationLogLine>> ReadAsync(int operationLogId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads only the <b>last</b> <paramref name="maxLines"/> logical lines of a log's file, streaming so
        /// no more than that many are ever held in memory at once — used by the live terminal to cap memory on
        /// a large, still-growing log. Empty if the file does not exist or <paramref name="maxLines"/> ≤ 0.
        /// </summary>
        Task<IReadOnlyList<OperationLogLine>> ReadTailAsync(int operationLogId, int maxLines, CancellationToken cancellationToken = default);

        /// <summary>True if any line of the log's file contains <paramref name="text"/> (case-insensitive).</summary>
        Task<bool> ContainsAsync(int operationLogId, string text, CancellationToken cancellationToken = default);

        /// <summary>Deletes the log's file (best-effort; a missing file is not an error).</summary>
        void Delete(int operationLogId);

        /// <summary>Deletes every log file in the store.</summary>
        void DeleteAll();
    }
}
