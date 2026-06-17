using System.ComponentModel.DataAnnotations.Schema;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A logged operation (e.g. a backup run). Has one-to-many <see cref="OperationLogDetail"/>
    /// lines (cascade delete). The severity of the operation as a whole is the header-level
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

        public ICollection<OperationLogDetail> Details { get; set; } = new List<OperationLogDetail>();

        /// <summary>
        /// Number of detail lines, populated by <c>IOperationLogService.GetPageAsync</c> for the
        /// grid (so a detail-less log doesn't show an expand control). Not persisted.
        /// </summary>
        [NotMapped]
        public int DetailCount { get; set; }
    }
}
