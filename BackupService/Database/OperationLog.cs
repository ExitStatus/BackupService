using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A logged operation (e.g. a backup run). Has one-to-many <see cref="OperationLogDetail"/>
    /// lines (cascade delete). The severity of the operation as a whole is the header-level
    /// <see cref="Level"/>; detail lines carry only their message.
    /// </summary>
    public class OperationLog
    {
        public int Id { get; set; }

        /// <summary>Unbounded name/title of the operation (TEXT / nvarchar(max)).</summary>
        public required string Name { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>Severity of the operation as a whole (set when the log is created).</summary>
        public OperationLogLevel Level { get; set; }

        public ICollection<OperationLogDetail> Details { get; set; } = new List<OperationLogDetail>();
    }
}
