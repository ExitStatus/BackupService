using BackupService.Database;

namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// The built-in application-database backup (Settings → Database Backup): owns the single-row
    /// <see cref="DatabaseBackupSettings"/>, runs a backup (consistent snapshot → zip → target, with
    /// keep-newest-N retention), lists the backups in the target folder, and stages a restore that
    /// <see cref="PendingRestoreApplier"/> applies on the next startup.
    /// </summary>
    public interface IDatabaseBackupService
    {
        /// <summary>The settings row (lazily seeded with defaults).</summary>
        Task<DatabaseBackupSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the settings (<paramref name="maxBackups"/> clamped to ≥ 1) and writes an operation
        /// log describing them. The caller must also call <see cref="IDatabaseBackupScheduler.SyncAsync"/>
        /// so the schedule change takes effect (the service can't — it would be a DI cycle).
        /// </summary>
        Task UpdateSettingsAsync(bool enabled, int? targetConnectionId, string targetFolder, string? scheduleCron, int maxBackups, CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs one backup now: VACUUMs a consistent snapshot of the live database, zips it, crash-safe
        /// copies it into the target (local or connection) and prunes to the newest MaxBackups — all under
        /// one operation log ("[Manual] "-prefixed when <paramref name="manual"/>). Only one backup runs
        /// at a time (a second call is skipped). Returns whether a backup was created.
        /// </summary>
        Task<bool> RunBackupAsync(bool manual, CancellationToken cancellationToken = default);

        /// <summary>The backups in the configured target folder, newest first (foreign files ignored).</summary>
        Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stages a restore of <paramref name="fileName"/> (one of <see cref="ListBackupsAsync"/>): copies
        /// the zip locally, extracts its database file into the pending-restore slot and logs it. The
        /// restore is applied by <see cref="PendingRestoreApplier"/> on the next startup — restoring a
        /// live SQLite file in-process would race its open connections. Throws with a friendly message
        /// on failure.
        /// </summary>
        Task StageRestoreAsync(string fileName, CancellationToken cancellationToken = default);
    }
}
