using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// One discrete run, recorded once per run for the dashboard. Covers both a backup run (a scheduled
    /// or manual "Run now" run routed through <c>BackupRunner</c>) and a scheduled-task run (routed
    /// through <c>ScheduledTaskRunner</c>); <see cref="Kind"/> distinguishes them. Continuous InstantSync
    /// watcher flushes are not runs and are not recorded here. Deleting the owning <see cref="Profile"/>
    /// or <see cref="ScheduledTask"/> cascade-deletes its run history.
    /// </summary>
    public class BackupRun
    {
        public int Id { get; set; }

        /// <summary>What kind of run this is (backup vs scheduled task).</summary>
        public RunKind Kind { get; set; }

        /// <summary>The profile this run belongs to (null for a scheduled-task run). Cascade-deleted with the profile.</summary>
        public int? ProfileId { get; set; }

        public Profile? Profile { get; set; }

        /// <summary>The scheduled task this run belongs to (null for a backup run). Cascade-deleted with the task.</summary>
        public int? ScheduledTaskId { get; set; }

        public ScheduledTask? ScheduledTask { get; set; }

        /// <summary>Denormalised profile type, so runs can be grouped by type directly. Unused for a scheduled-task run.</summary>
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

        /// <summary>Non-fatal issues (e.g. files skipped because they were locked by another process).</summary>
        public int Warnings { get; set; }

        /// <summary>Total bytes of data written to the target during the run (copies/updates/archives).</summary>
        public long BytesCopied { get; set; }

        /// <summary>True for a manual "Run now" run; false for a scheduled run.</summary>
        public bool Manual { get; set; }

        /// <summary>
        /// The <see cref="OperationLog"/> id for this run, for drill-through. A loose reference (no
        /// FK constraint) so log retention can purge the log without affecting run history.
        /// </summary>
        public int? OperationLogId { get; set; }
    }
}
