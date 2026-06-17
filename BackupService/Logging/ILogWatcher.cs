namespace BackupService.Logging
{
    /// <summary>
    /// Notifies subscribers when operation-log data changes, so views (the Logs panel) can refresh on
    /// a push rather than by polling. Notifications are <b>coalesced</b>: a burst of writes raises at
    /// most one <see cref="Changed"/> per debounce window, and during sustained writing it fires about
    /// once per window (it is not reset by each write, so a long run still gets periodic notifications
    /// instead of none until it stops). Registered as a singleton; all members are thread-safe.
    /// </summary>
    public interface ILogWatcher
    {
        /// <summary>
        /// Raised (on a background thread) after log data has changed and the debounce window settles.
        /// Subscribers must marshal back to their own context (e.g. a Blazor component uses
        /// <c>InvokeAsync</c>) and keep handlers cheap/non-throwing.
        /// </summary>
        event Action? Changed;

        /// <summary>
        /// Records that a log item was written (header created, detail line appended, or summary
        /// revised). Cheap and thread-safe — safe to call once per log line.
        /// </summary>
        void Notify();
    }
}
