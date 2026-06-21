using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// A watched source/target folder mapping within an <see cref="Enumerations.ProfileType.InstantSync"/>
    /// <see cref="Profile"/>. While the profile is enabled a file watcher is attached to
    /// <see cref="SourceFolder"/>; changes are debounced by <see cref="DebounceMilliseconds"/> and then
    /// mirrored into <see cref="TargetFolder"/>.
    /// </summary>
    public class InstantSyncItem
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

        /// <summary>When set, the source is on this connection and <see cref="SourceFolder"/> is relative to its root.
        /// A remote source can't be watched for live changes (only a manual reconcile syncs it).</summary>
        public int? SourceConnectionId { get; set; }

        public Connection? SourceConnection { get; set; }

        /// <summary>When set, the target is on this connection and <see cref="TargetFolder"/> is relative to its root.</summary>
        public int? TargetConnectionId { get; set; }

        public Connection? TargetConnection { get; set; }

        /// <summary>How long the source must be quiet before queued changes are copied.</summary>
        public int DebounceMilliseconds { get; set; }

        public bool IncludeSubFolders { get; set; }

        public bool AllowDeletions { get; set; }
    }
}
