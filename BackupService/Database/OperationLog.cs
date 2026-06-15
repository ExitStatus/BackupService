namespace BackupService.Database
{
    /// <summary>
    /// A logged operation (e.g. a backup run). Has one-to-many <see cref="OperationLogDetail"/>
    /// lines (cascade delete).
    /// </summary>
    public class OperationLog
    {
        public int Id { get; set; }

        /// <summary>Unbounded name/title of the operation (TEXT / nvarchar(max)).</summary>
        public required string Name { get; set; }

        public DateTimeOffset TimestampUtc { get; set; }

        public ICollection<OperationLogDetail> Details { get; set; } = new List<OperationLogDetail>();
    }
}
