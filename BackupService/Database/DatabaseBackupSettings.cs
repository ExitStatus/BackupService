using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// Single-row settings for the built-in application-database backup (Settings → Database Backup).
    /// One row, like <see cref="LogRetentionSettings"/>; seeded lazily with the defaults below.
    /// The target can be local or a connection (<see cref="TargetConnectionId"/> null = local;
    /// <see cref="TargetFolder"/> is relative to the connection's root when one is set). Deleting the
    /// connection nulls the reference (FK <c>SET NULL</c>) rather than blocking the delete.
    /// </summary>
    public class DatabaseBackupSettings
    {
        public int Id { get; set; }

        /// <summary>Whether automatic (scheduled) database backups run. Defaults to off.</summary>
        public bool Enabled { get; set; }

        /// <summary>The connection the backups are written to, or null for a local folder.</summary>
        public int? TargetConnectionId { get; set; }

        public Connection? TargetConnection { get; set; }

        /// <summary>The target folder (relative to the connection root when one is set).</summary>
        [MaxLength(1024)]
        public string TargetFolder { get; set; } = string.Empty;

        /// <summary>The automatic backup schedule as a 5-field cron string (null = not scheduled).</summary>
        [MaxLength(128)]
        public string? Schedule { get; set; }

        /// <summary>How many backups to keep in the target folder — the oldest beyond this are deleted.</summary>
        public int MaxBackups { get; set; } = 5;
    }
}
