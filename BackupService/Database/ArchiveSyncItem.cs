using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One archive definition within an <see cref="Enumerations.ProfileType.ArchiveSync"/>
    /// <see cref="Profile"/>. When the profile's schedule fires, a ZIP of <see cref="SourceFolder"/>
    /// is built and crash-safe copied into <see cref="TargetFolder"/> as
    /// <c>{FileName}_{timestamp}.zip</c> (with an <c>_L{level}_</c> token under grandfather-father-son
    /// retention), then old archives are pruned per <see cref="RetentionMode"/>.
    /// </summary>
    public class ArchiveSyncItem
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

        /// <summary>Base name for the generated archive; a timestamp (and GFS level token) is appended.</summary>
        [MaxLength(256)]
        public required string FileName { get; set; }

        public bool IncludeSubFolders { get; set; }

        public ArchiveRetentionMode RetentionMode { get; set; }

        /// <summary>Archives kept — total for <see cref="ArchiveRetentionMode.KeepLastN"/>, per level for GFS.</summary>
        public int RetentionCount { get; set; }

        /// <summary>Number of GFS levels (son/father/grandfather/…); 1 for <see cref="ArchiveRetentionMode.KeepLastN"/>.</summary>
        public int MaxLevels { get; set; } = 1;

        /// <summary>Monotonic count of archives created so far; drives the GFS promotion cadence.</summary>
        public int RunCount { get; set; }

        /// <summary>Include/exclude rules that filter which source files are archived.</summary>
        public ICollection<ArchiveSyncFilter> Filters { get; set; } = new List<ArchiveSyncFilter>();
    }
}
