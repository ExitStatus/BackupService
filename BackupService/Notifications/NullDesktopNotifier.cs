using BackupService.Enumerations;

namespace BackupService.Notifications
{
    /// <summary>No-op <see cref="IDesktopNotifier"/> used off Windows (tray notifications are Windows-only).</summary>
    public sealed class NullDesktopNotifier : IDesktopNotifier
    {
        public void NotifyBackupStarted(string profileName, ProfileType type)
        {
            // No desktop notifications on this platform.
        }

        public void NotifyBackupCompleted(string profileName, ProfileType type, RunOutcome outcome)
        {
            // No desktop notifications on this platform.
        }

        public void NotifyTaskCompleted(string taskName, RunOutcome outcome)
        {
            // No desktop notifications on this platform.
        }

        public void NotifyDeviceConnected(string deviceName)
        {
            // No desktop notifications on this platform.
        }

        public void NotifyDeviceDisconnected(string deviceName)
        {
            // No desktop notifications on this platform.
        }
    }
}
