using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A backup profile: a named group of one or more <see cref="FolderPair"/> records.
    /// </summary>
    public class Profile
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        public ProfileType Type { get; set; }

        /// <summary>Whether the profile participates in backups. Defaults to enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Cron-style schedule string; null/empty when not scheduled.</summary>
        [MaxLength(256)]
        public string? Schedule { get; set; }

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset? DateLastRun { get; set; }

        public ICollection<FolderPair> FolderPairs { get; set; } = new List<FolderPair>();

        public ICollection<InstantSyncItem> InstantSyncItems { get; set; } = new List<InstantSyncItem>();
    }
}
