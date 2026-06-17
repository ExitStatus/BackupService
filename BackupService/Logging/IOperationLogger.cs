using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// A live handle to an <see cref="Database.OperationLog"/> record. Append detail lines via
    /// <see cref="AppendAsync(string[])"/>; each <c>message</c> becomes its own
    /// <see cref="Database.OperationLogDetail"/> row with the next sequence number (so passing
    /// several messages in one call writes several rows rather than one delimited string). Each
    /// line carries a <see cref="OperationLogLevel"/> and the header's
    /// <see cref="Database.OperationLog.Level"/> is kept in step as the most severe line seen.
    /// </summary>
    public interface IOperationLogger
    {
        /// <summary>Id of the OperationLog record this logger appends detail lines to.</summary>
        int OperationLogId { get; }

        /// <summary>Appends one <see cref="OperationLogLevel.Info"/> detail row per supplied message.</summary>
        Task AppendAsync(params string[] messages);

        /// <summary>
        /// Appends one detail row per supplied message at the given <paramref name="level"/>,
        /// raising the header's <see cref="Database.OperationLog.Level"/> if the line is more severe.
        /// </summary>
        Task AppendAsync(OperationLogLevel level, params string[] messages);

        /// <summary>
        /// Updates the log header's primary message (<see cref="Database.OperationLog.Name"/>) — e.g.
        /// to replace a "started" title with a final "succeeded/failed in {duration}" summary. The
        /// header level is derived from the detail lines; <paramref name="level"/> acts as a floor,
        /// raising it if more severe (e.g. forcing Error on a run that failed before logging an
        /// error line) but never lowering a level the lines already escalated to.
        /// </summary>
        Task SetSummaryAsync(string message, OperationLogLevel level);

        /// <summary>
        /// Appends a single <see cref="OperationLogLevel.Error"/> detail line (which raises the
        /// header to Error). When <paramref name="exception"/> is supplied, its full detail
        /// (including the stack trace) is appended to the message.
        /// </summary>
        Task ErrorAsync(string message, Exception? exception = null);
    }
}
