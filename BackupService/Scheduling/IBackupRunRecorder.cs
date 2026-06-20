using BackupService.Enumerations;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Records one structured <c>Database.BackupRun</c> row per discrete backup run, for the
    /// dashboard. Called by each <see cref="IProfileTypeHandler"/> from the <c>finally</c> that
    /// owns the run's operation log, so every scheduled/manual run is captured exactly once.
    /// </summary>
    public interface IBackupRunRecorder
    {
        Task RecordAsync(
            int profileId,
            ProfileType type,
            bool manual,
            DateTimeOffset startedUtc,
            double durationMs,
            BackupResult counts,
            RunOutcome outcome,
            int? operationLogId,
            CancellationToken cancellationToken = default);
    }
}
