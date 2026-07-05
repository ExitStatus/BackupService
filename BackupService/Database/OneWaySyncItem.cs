using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A source/target folder mapping within a <see cref="Profile"/>. The owning profile
    /// carries the cron schedule that drives the backup.
    /// </summary>
    public class OneWaySyncItem
    {
        public int Id { get; set; }

        public int ProfileId { get; set; }

        public Profile? Profile { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        [MaxLength(1024)]
        public required string SourceFolder { get; set; }

        [MaxLength(1024)]
        public required string TargetFolder { get; set; }

        public bool AllowDeletions { get; set; }

        public bool IncludeSubFolders { get; set; }

        public OverwriteBehaviour OverwriteBehaviour { get; set; }

        public OneWaySyncStatus Status { get; set; }

        public OneWaySyncLastRunStatus LastRunStatus { get; set; }

        /// <summary>Include/exclude rules that filter which source files are synced.</summary>
        public ICollection<OneWaySyncFilter> Filters { get; set; } = new List<OneWaySyncFilter>();
    }
}
