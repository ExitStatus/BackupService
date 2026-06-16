using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// A live handle to an <see cref="Database.OperationLog"/> record. Append detail lines via
    /// <see cref="AppendAsync"/>; each <c>message</c> becomes its own
    /// <see cref="Database.OperationLogDetail"/> row with the next sequence number (so passing
    /// several messages in one call writes several rows rather than one delimited string). The
    /// severity is fixed on the log header when it is created, not per line.
    /// </summary>
    public interface IOperationLogger
    {
        /// <summary>Id of the OperationLog record this logger appends detail lines to.</summary>
        int OperationLogId { get; }

        /// <summary>Appends one detail row per supplied message.</summary>
        Task AppendAsync(params string[] messages);

        /// <summary>
        /// Updates the log header's primary message (<see cref="Database.OperationLog.Name"/>) and
        /// severity — e.g. to replace a "started" title with a final "succeeded/failed in {duration}"
        /// summary at the appropriate level, without adding another log record.
        /// </summary>
        Task SetSummaryAsync(string message, OperationLogLevel level);

        /// <summary>
        /// Appends a single detail line. When <paramref name="exception"/> is supplied, its full
        /// detail (including the stack trace) is appended to the message.
        /// </summary>
        Task ErrorAsync(string message, Exception? exception = null);
    }
}
