using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Default <see cref="IBackupRunRecorder"/>. Inserts one <see cref="BackupRun"/> row via the
    /// DbContext factory (a short-lived context per call).
    /// </summary>
    public sealed class BackupRunRecorder(IDatabaseContextFactory contextFactory) : IBackupRunRecorder
    {
        public async Task RecordAsync(
            int profileId,
            ProfileType type,
            bool manual,
            DateTimeOffset startedUtc,
            double durationMs,
            BackupResult counts,
            RunOutcome outcome,
            int? operationLogId,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            db.BackupRuns.Add(new BackupRun
            {
                ProfileId = profileId,
                Type = type,
                Manual = manual,
                StartedUtc = startedUtc,
                DurationMs = (long)Math.Round(durationMs),
                Outcome = outcome,
                Copied = counts.Copied,
                Updated = counts.Updated,
                Deleted = counts.Deleted,
                Errors = counts.Errors,
                Warnings = counts.Warnings,
                BytesCopied = counts.BytesCopied,
                OperationLogId = operationLogId,
            });

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
