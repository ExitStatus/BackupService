using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Reads operation logs for display: a page of <see cref="OperationLog"/> headers
    /// (newest first) and, on demand, the <see cref="OperationLogDetail"/> lines for one log.
    /// </summary>
    public interface IOperationLogService
    {
        /// <summary>
        /// A page of log headers (newest first). When <paramref name="filter"/> is non-empty,
        /// only logs whose <c>Name</c> contains it are returned; if
        /// <paramref name="includeMessages"/> is also set, logs with a matching detail
        /// <c>Message</c> are included too. When <paramref name="level"/> is supplied, only logs
        /// of that level are returned. Filters combine (AND).
        /// </summary>
        Task<PagedResult<OperationLog>> GetPageAsync(
            int pageNumber,
            int pageSize,
            string? filter = null,
            bool includeMessages = false,
            OperationLogLevel? level = null,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<OperationLogDetail>> GetDetailsAsync(int operationLogId, CancellationToken cancellationToken = default);
    }
}
