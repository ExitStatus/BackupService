using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One bidirectional folder mapping within a <see cref="Enumerations.ProfileType.TwoWaySync"/>
    /// <see cref="Profile"/>. Unlike a one-way <see cref="OneWaySyncItem"/>, changes made on <b>either</b> side
    /// (source or target) are reconciled to the other. The engine keeps a persisted baseline of the last-synced
    /// state (see <c>TwoWaySyncStateStore</c>) so it can tell a deletion from a new file; a file changed on both
    /// sides since the baseline is a conflict, resolved per <see cref="ConflictResolution"/>.
    /// </summary>
    public class TwoWaySyncItem
    {
        public int Id { get; set; }

        public int ProfileId { get; set; }

        public Profile? Profile { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        /// <summary>The source (left) folder, relative to the profile's source connection root (or local).</summary>
        [MaxLength(1024)]
        public required string SourceFolder { get; set; }

        /// <summary>The target (right) folder, relative to the profile's target connection root (or local).</summary>
        [MaxLength(1024)]
        public required string TargetFolder { get; set; }

        public bool IncludeSubFolders { get; set; }

        /// <summary>How a genuine conflict (the same file changed on both sides since the baseline) is resolved.</summary>
        public ConflictResolution ConflictResolution { get; set; }

        /// <summary>
        /// When true, a file deleted on one side (since the baseline) is deleted from the other side too. When
        /// false, the deletion is ignored and the file is restored from the surviving side. Defaults to true.
        /// </summary>
        public bool PropagateDeletions { get; set; } = true;

        /// <summary>Include/exclude rules that filter which files are synced (both directions).</summary>
        public ICollection<TwoWaySyncFilter> Filters { get; set; } = new List<TwoWaySyncFilter>();
    }
}
