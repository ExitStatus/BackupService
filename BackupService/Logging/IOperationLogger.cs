namespace BackupService.Logging
{
    /// <summary>
    /// A live handle to an <see cref="Database.OperationLog"/> record. Append detail lines via
    /// the level-specific methods; each <c>message</c> becomes its own
    /// <see cref="Database.OperationLogDetail"/> row with the next sequence number (so passing
    /// several messages in one call writes several rows rather than one delimited string).
    /// </summary>
    public interface IOperationLogger
    {
        /// <summary>Id of the OperationLog record this logger appends detail lines to.</summary>
        int OperationLogId { get; }

        Task InfoAsync(params string[] messages);

        Task WarningAsync(params string[] messages);

        Task DebugAsync(params string[] messages);

        /// <summary>
        /// Logs a single error. When <paramref name="exception"/> is supplied, its full detail
        /// (including the stack trace) is appended to the message.
        /// </summary>
        Task ErrorAsync(string message, Exception? exception = null);
    }
}
