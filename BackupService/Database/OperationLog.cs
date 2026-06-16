using System.ComponentModel.DataAnnotations.Schema;
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

        /// <summary>
        /// The profile this log relates to, if any. Null for logs not tied to a specific
        /// profile. Deleting the profile cascade-deletes its logs.
        /// </summary>
        public int? ProfileId { get; set; }

        public Profile? Profile { get; set; }

        public ICollection<OperationLogDetail> Details { get; set; } = new List<OperationLogDetail>();

        /// <summary>
        /// Number of detail lines, populated by <c>IOperationLogService.GetPageAsync</c> for the
        /// grid (so a detail-less log doesn't show an expand control). Not persisted.
        /// </summary>
        [NotMapped]
        public int DetailCount { get; set; }
    }
}
