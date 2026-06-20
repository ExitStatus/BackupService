using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One discrete backup run (a scheduled or manual "Run now" run routed through
    /// <c>BackupRunner</c>), recorded once per run by each handler so the dashboard has structured
    /// statistics. Continuous InstantSync watcher flushes are not runs and are not recorded here.
    /// Deleting the owning <see cref="Profile"/> cascade-deletes its run history (required FK).
    /// </summary>
    public class BackupRun
    {
        public int Id { get; set; }

        /// <summary>The profile this run belongs to. Cascade-deleted with the profile.</summary>
        public int ProfileId { get; set; }

        public Profile? Profile { get; set; }

        /// <summary>Denormalised profile type, so runs can be grouped by type directly.</summary>
        public ProfileType Type { get; set; }

        /// <summary>When the run started (UTC).</summary>
        public DateTimeOffset StartedUtc { get; set; }

        /// <summary>How long the run took, in milliseconds.</summary>
        public long DurationMs { get; set; }

        /// <summary>The run's overall outcome.</summary>
        public RunOutcome Outcome { get; set; }

        public int Copied { get; set; }

        public int Updated { get; set; }

        public int Deleted { get; set; }

        public int Errors { get; set; }

        /// <summary>True for a manual "Run now" run; false for a scheduled run.</summary>
        public bool Manual { get; set; }

        /// <summary>
        /// The <see cref="OperationLog"/> id for this run, for drill-through. A loose reference (no
        /// FK constraint) so log retention can purge the log without affecting run history.
        /// </summary>
        public int? OperationLogId { get; set; }
    }
}
