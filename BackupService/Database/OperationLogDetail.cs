using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A single detail line within an <see cref="OperationLog"/>. Ordered within its log by
    /// <see cref="Sequence"/>.
    /// </summary>
    public class OperationLogDetail
    {
        public int Id { get; set; }

        public int OperationLogId { get; set; }

        public OperationLog? OperationLog { get; set; }

        /// <summary>Unbounded log message (TEXT / nvarchar(max)).</summary>
        public required string Message { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }

        public OperationLogLevel Level { get; set; }

        /// <summary>Ordering of this line within its operation log.</summary>
        public int Sequence { get; set; }
    }
}
