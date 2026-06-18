using BackupService.Database;

namespace BackupService.Logging
{
    /// <summary>
    /// Owns the log-retention policy: how many days of authentication and operation history to keep,
    /// and the purge that removes anything older. The purge runs <b>after a log operation</b> (see the
    /// hooks in <c>OperationLogFactory</c> / <c>AuthenticationHistoryService</c>) but at most once per
    /// day — an in-memory marker records the last purge date and skips until the next day. Singleton.
    /// </summary>
    public interface ILogRetentionService
    {
        /// <summary>Current retention settings, lazily seeding the default row (7/30) if none exists.</summary>
        Task<LogRetentionSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>Persists new retention day counts (each clamped to a minimum of 1).</summary>
        Task UpdateSettingsAsync(int authenticationLogRetentionDays, int operationLogRetentionDays, CancellationToken cancellationToken = default);

        /// <summary>
        /// Purges log rows older than their retention — but only once per (UTC) day. Cheap and safe to
        /// call after every log write: a same-day repeat is a quick no-op. Never throws (failures are
        /// logged and retried on a later call).
        /// </summary>
        Task PurgeIfDueAsync(CancellationToken cancellationToken = default);
    }
}
