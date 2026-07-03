using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Reads operation logs for display: a page of <see cref="OperationLog"/> headers
    /// (newest first) and, on demand, the detail lines for one log (read from its on-disk
    /// log file — see <see cref="IOperationLogFileStore"/>).
    /// </summary>
    public interface IOperationLogService
    {
        /// <summary>
        /// A page of log headers (newest first, each with its <c>Profile</c> loaded). When
        /// <paramref name="filter"/> is non-empty, only logs whose <c>Name</c> contains it are
        /// returned; if <paramref name="includeMessages"/> is also set, logs whose file contains a
        /// matching line are included too (matched by scanning each log's file). When
        /// <paramref name="level"/> is supplied, only logs of that level are returned; when
        /// <paramref name="profileId"/> is supplied, only logs tied to that profile. Filters combine (AND).
        /// </summary>
        Task<PagedResult<OperationLog>> GetPageAsync(
            int pageNumber,
            int pageSize,
            string? filter = null,
            bool includeMessages = false,
            OperationLogLevel? level = null,
            int? profileId = null,
            CancellationToken cancellationToken = default);

        /// <summary>The detail lines of one log, parsed from its log file (empty if it has none).</summary>
        Task<IReadOnlyList<OperationLogLine>> GetDetailsAsync(int operationLogId, CancellationToken cancellationToken = default);

        /// <summary>
        /// The most recent <paramref name="maxLines"/> detail lines of one log (memory-bounded), for the live
        /// terminal that tails a still-growing log without holding the whole file.
        /// </summary>
        Task<IReadOnlyList<OperationLogLine>> GetRecentDetailsAsync(int operationLogId, int maxLines, CancellationToken cancellationToken = default);

        /// <summary>
        /// A window of one log's lines plus whole-file totals/level counts (one streaming pass; at most
        /// <paramref name="take"/> lines held). Null <paramref name="skip"/> = the tail. Used by the Logs
        /// terminal to show the end of a huge log and walk backwards on demand.
        /// </summary>
        Task<OperationLogWindow> GetDetailWindowAsync(int operationLogId, int? skip, int take, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streaming search over one log's lines (case-insensitive text contains + optional level set),
        /// returning at most <paramref name="maxMatches"/> matching lines and the total match count.
        /// </summary>
        Task<OperationLogSearch> SearchDetailsAsync(int operationLogId, string? text, IReadOnlyCollection<OperationLogLevel>? levels, int maxMatches, CancellationToken cancellationToken = default);
    }
}
