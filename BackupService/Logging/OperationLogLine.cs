using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// A single parsed line of an operation log, read back from its on-disk log file. Replaces the old
    /// <c>OperationLogDetail</c> database row: detail lines are no longer stored in the database (they were
    /// far too many and too write-heavy) but appended to a per-log text file and parsed on demand for the
    /// Logs terminal view. <see cref="Timestamp"/> is the local wall-clock time the line was written (the
    /// same value shown in the terminal); a <see cref="Message"/> may contain embedded newlines (a
    /// consolidated multi-line entry, e.g. an ArchiveSync file list).
    /// </summary>
    public sealed record OperationLogLine(OperationLogLevel Level, DateTimeOffset Timestamp, string Message);
}
