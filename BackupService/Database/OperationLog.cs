using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A logged operation (e.g. a backup run). Its detail lines are no longer stored as database rows
    /// (they were far too many and too write-heavy); instead they are appended to a per-log text file
    /// referenced by <see cref="LogFile"/> and read back on demand for the Logs terminal view (see
    /// <c>IOperationLogFileStore</c>). The severity of the operation as a whole is the header-level
    /// <see cref="Level"/>, derived as the most severe of its detail lines' levels (maintained as
    /// lines are appended; see <c>OperationLogger</c>).
    /// </summary>
    public class OperationLog
    {
        public int Id { get; set; }

        /// <summary>Unbounded name/title of the operation (TEXT / nvarchar(max)).</summary>
        public required string Name { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// Severity of the operation as a whole — the most severe of its detail lines' levels
        /// (Debug/Info → Info, any Warning → Warning, any Error → Error). Maintained by
        /// <c>OperationLogger</c> as lines are appended.
        /// </summary>
        public OperationLogLevel Level { get; set; }

        /// <summary>
        /// The profile this log relates to, if any. Null for logs not tied to a specific
        /// profile. Deleting the profile cascade-deletes its logs.
        /// </summary>
        public int? ProfileId { get; set; }

        public Profile? Profile { get; set; }

        /// <summary>
        /// The name of the on-disk log file holding this log's detail lines (relative to the operation-logs
        /// directory; see <c>IOperationLogFileStore</c>). Null for a self-describing/detail-less log (the
        /// message lives entirely in <see cref="Name"/>), which the Logs grid renders with no expand control.
        /// Set when the first line is written.
        /// </summary>
        public string? LogFile { get; set; }
    }
}
