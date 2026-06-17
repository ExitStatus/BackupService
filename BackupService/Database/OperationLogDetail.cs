using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A single detail line within an <see cref="OperationLog"/>. Ordered within its log by
    /// <see cref="Sequence"/>. Each line carries its own <see cref="Level"/>; the parent
    /// <see cref="OperationLog.Level"/> is derived as the most severe of its lines' levels.
    /// </summary>
    public class OperationLogDetail
    {
        public int Id { get; set; }

        public int OperationLogId { get; set; }

        public OperationLog? OperationLog { get; set; }

        /// <summary>Unbounded log message (TEXT / nvarchar(max)).</summary>
        public required string Message { get; set; }

        /// <summary>Severity of this individual line.</summary>
        public OperationLogLevel Level { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>Ordering of this line within its operation log.</summary>
        public int Sequence { get; set; }
    }
}
