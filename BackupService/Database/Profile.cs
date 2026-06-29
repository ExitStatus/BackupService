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

        public ProfileType Type { get; set; }

        /// <summary>
        /// When set, the source for every row lives on this <see cref="Connection"/> and each row's
        /// <c>SourceFolder</c> is interpreted relative to the connection's root. Null = local on this machine.
        /// Always null for InstantSync/LightroomArchive (their source is watcher-driven, local-only).
        /// </summary>
        public int? SourceConnectionId { get; set; }

        public Connection? SourceConnection { get; set; }

        /// <summary>
        /// When set, the target for every row lives on this <see cref="Connection"/> and each row's
        /// <c>TargetFolder</c> is interpreted relative to the connection's root. Null = local on this machine.
        /// </summary>
        public int? TargetConnectionId { get; set; }

        public Connection? TargetConnection { get; set; }

        /// <summary>Whether the profile participates in backups. Defaults to enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Cron-style schedule string; null/empty when not scheduled.</summary>
        [MaxLength(256)]
        public string? Schedule { get; set; }

        /// <summary>
        /// LightroomArchive only: the local Lightroom catalog folder scanned (recursively) for raw sidecars
        /// matching each copied file. Null for other profile types.
        /// </summary>
        [MaxLength(1024)]
        public string? LightroomFolder { get; set; }

        /// <summary>
        /// LightroomArchive only: the comma-separated raw file extensions to match (e.g. <c>.DNG,.ARW</c>).
        /// </summary>
        [MaxLength(256)]
        public string? RawFormats { get; set; }

        /// <summary>
        /// LightroomArchive only: the name of the sub-folder (beside each copied file) the matching raws are
        /// copied into (e.g. <c>RAW</c>).
        /// </summary>
        [MaxLength(256)]
        public string? RawFolderName { get; set; }

        /// <summary>
        /// When the next scheduled run should occur (recorded by <c>BackupSchedulerService</c> for scheduled
        /// profiles). Null when not scheduled. Persisted so a missed run can be detected after a restart.
        /// </summary>
        public DateTimeOffset? DateNextRun { get; set; }

        /// <summary>
        /// When true, if the service was not running at <see cref="DateNextRun"/> the profile runs immediately
        /// on the next startup (catch-up). Only meaningful for scheduled profile types.
        /// </summary>
        public bool HandleMissedSync { get; set; }

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset? DateLastRun { get; set; }

        public ICollection<FolderPair> FolderPairs { get; set; } = new List<FolderPair>();

        public ICollection<InstantSyncItem> InstantSyncItems { get; set; } = new List<InstantSyncItem>();

        public ICollection<ArchiveSyncItem> ArchiveSyncItems { get; set; } = new List<ArchiveSyncItem>();

        public ICollection<LightroomArchiveItem> LightroomArchiveItems { get; set; } = new List<LightroomArchiveItem>();
    }
}
