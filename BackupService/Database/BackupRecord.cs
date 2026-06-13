using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// A single backup run: where it copied from/to, when, and how it finished.
    /// Placeholder schema — extend as the backup feature takes shape.
    /// </summary>
    public class BackupRecord
    {
        public int Id { get; set; }

        [MaxLength(1024)]
        public required string SourcePath { get; set; }

        [MaxLength(1024)]
        public required string DestinationPath { get; set; }

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset? CompletedAtUtc { get; set; }

        public BackupStatus Status { get; set; }

        public long SizeBytes { get; set; }

        [MaxLength(2048)]
        public string? ErrorMessage { get; set; }
    }

    public enum BackupStatus
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
    }
}
