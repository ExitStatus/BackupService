namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Keeps the automatic database backup's live schedule in step with the settings — the
    /// single-entry counterpart of <c>IBackupScheduler</c>.
    /// </summary>
    public interface IDatabaseBackupScheduler
    {
        /// <summary>
        /// Re-reads the settings and (re)schedules the automatic backup (enabled + parseable cron) or
        /// unschedules it. The Database Backup panel calls this after every settings save.
        /// </summary>
        Task SyncAsync(CancellationToken cancellationToken = default);
    }
}
