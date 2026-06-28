using BackupService.Enumerations;

namespace BackupService.Notifications
{
    /// <summary>No-op <see cref="IRunCompletionNotifier"/> used off Windows (tray notifications are Windows-only).</summary>
    public sealed class NullRunCompletionNotifier : IRunCompletionNotifier
    {
        public void NotifyBackupCompleted(string profileName, ProfileType type, RunOutcome outcome)
        {
            // No desktop notifications on this platform.
        }

        public void NotifyTaskCompleted(string taskName, RunOutcome outcome)
        {
            // No desktop notifications on this platform.
        }
    }
}
