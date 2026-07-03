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
        /// <summary>The directory the log files live in (used by the raw-download endpoint).</summary>
        string RootDirectory { get; }

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

        /// <summary>
        /// Reads a window of a log's lines on one streaming pass (at most <paramref name="take"/> lines
        /// held): a null <paramref name="skip"/> returns the <b>tail</b> (the last <paramref name="take"/>
        /// lines), a set one returns lines [<paramref name="skip"/>, skip+take). The result also carries
        /// the file's total line count and per-level Warning/Error counts, gathered on the same pass.
        /// </summary>
        Task<OperationLogWindow> ReadWindowAsync(int operationLogId, int? skip, int take, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streaming search (grep semantics): lines whose message contains <paramref name="text"/>
        /// (case-insensitive; null/blank = no text filter) and whose level is in <paramref name="levels"/>
        /// (null/empty = all levels). Counts every match but returns at most <paramref name="maxMatches"/>
        /// lines, in file order.
        /// </summary>
        Task<OperationLogSearch> SearchAsync(int operationLogId, string? text, IReadOnlyCollection<OperationLogLevel>? levels, int maxMatches, CancellationToken cancellationToken = default);

        /// <summary>True if any line of the log's file contains <paramref name="text"/> (case-insensitive).</summary>
        Task<bool> ContainsAsync(int operationLogId, string text, CancellationToken cancellationToken = default);

        /// <summary>Deletes the log's file (best-effort; a missing file is not an error).</summary>
        void Delete(int operationLogId);

        /// <summary>Deletes every log file in the store.</summary>
        void DeleteAll();
    }
}
