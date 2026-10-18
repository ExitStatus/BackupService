using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// A profile's most recent run as the Profiles grid's Last Status chip shows it: the run log's level, and that
    /// log's id to open it in the Logs section (null when retention has purged the log — the level then comes from
    /// the run's outcome).
    /// </summary>
    public sealed record LastRunStatus(OperationLogLevel Level, int? LogId);
}
