using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// A watched source/target folder mapping within an <see cref="Enumerations.ProfileType.LightroomArchive"/>
    /// <see cref="Profile"/>. Behaves like an <see cref="InstantSyncItem"/> (a debounced live copy from
    /// <see cref="SourceFolder"/> to <see cref="TargetFolder"/>) but, whenever a file is copied, the matching
    /// raw sidecar(s) are pulled from the profile's Lightroom folder into a RAW sub-folder beside the copy
    /// (see the profile-level Lightroom settings and <c>LightroomArchiveProcessor</c>).
    ///
    /// The source is always local (a live <see cref="FileSystemWatcher"/> can't watch a remote share); the
    /// target may be local or on a <see cref="Connection"/>.
    /// </summary>
    public class LightroomArchiveItem
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

        /// <summary>When set, the target is on this connection and <see cref="TargetFolder"/> is relative to its root.</summary>
        public int? TargetConnectionId { get; set; }

        public Connection? TargetConnection { get; set; }

        /// <summary>How long the source must be quiet before queued changes are copied.</summary>
        public int DebounceMilliseconds { get; set; }

        public bool IncludeSubFolders { get; set; }

        public bool AllowDeletions { get; set; }
    }
}
