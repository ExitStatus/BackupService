using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// What kind of work a <c>Database.BackupRun</c> row records. <see cref="Backup"/> is 0 so existing
    /// run-history rows default to it when the column is added.
    /// </summary>
    public enum RunKind
    {
        /// <summary>A backup profile run (FolderPair / InstantSync / ArchiveSync / LightroomArchive).</summary>
        [Description("Backup")]
        Backup = 0,

        /// <summary>A scheduled-task run (one or more ordered commands).</summary>
        [Description("Scheduled Task")]
        ScheduledTask = 1,
    }
}
