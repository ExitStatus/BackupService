using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// A cron-scheduled task: a named group of one or more ordered <see cref="ScheduledTaskStep"/>
    /// commands run in sequence when its <see cref="Schedule"/> fires. Distinct from a backup
    /// <see cref="Profile"/> — it runs OS processes rather than copying files — but uses the same
    /// scheduling machinery and records run history the same way (a <see cref="BackupRun"/> per run).
    /// </summary>
    public class ScheduledTask
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }

        /// <summary>Whether the task participates in scheduling. Defaults to enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Cron-style schedule string; null/empty when not scheduled.</summary>
        [MaxLength(256)]
        public string? Schedule { get; set; }

        /// <summary>
        /// When the next scheduled run should occur (recorded by <c>ScheduledTaskSchedulerService</c>).
        /// Null when not scheduled. Persisted so a missed run can be detected after a restart.
        /// </summary>
        public DateTimeOffset? DateNextRun { get; set; }

        /// <summary>
        /// When true, if the service was not running at <see cref="DateNextRun"/> the task runs immediately
        /// on the next startup (catch-up).
        /// </summary>
        public bool HandleMissedSync { get; set; }

        public DateTimeOffset DateCreated { get; set; }

        public DateTimeOffset? DateLastRun { get; set; }

        /// <summary>The ordered commands run when the task fires (ascending <see cref="ScheduledTaskStep.Order"/>).</summary>
        public ICollection<ScheduledTaskStep> Steps { get; set; } = new List<ScheduledTaskStep>();
    }
}
