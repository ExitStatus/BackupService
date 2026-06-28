using BackupService.Enumerations;

namespace BackupService.Notifications
{
    /// <summary>
    /// Raises a desktop notification when a discrete run completes. Implemented on Windows by
    /// <c>WindowsTrayService</c> (a tray balloon, gated by the "Allow notifications" option); a no-op
    /// elsewhere (<see cref="NullRunCompletionNotifier"/>). Calls are synchronous and fire-and-forget — the
    /// Windows impl just queues the balloon to its message-loop thread.
    /// </summary>
    public interface IRunCompletionNotifier
    {
        /// <summary>A scheduled or manual backup run finished.</summary>
        void NotifyBackupCompleted(string profileName, ProfileType type, RunOutcome outcome);

        /// <summary>A scheduled-task run finished.</summary>
        void NotifyTaskCompleted(string taskName, RunOutcome outcome);
    }
}
