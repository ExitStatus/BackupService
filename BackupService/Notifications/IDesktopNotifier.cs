using BackupService.Enumerations;

namespace BackupService.Notifications
{
    /// <summary>
    /// Raises desktop notifications for app events — discrete run completions and USB device connect/disconnect.
    /// Implemented on Windows by <c>WindowsTrayService</c> (a tray balloon, gated by the "Allow notifications"
    /// option); a no-op elsewhere (<see cref="NullDesktopNotifier"/>). Calls are synchronous and fire-and-forget —
    /// the Windows impl just queues the balloon to its message-loop thread.
    /// </summary>
    public interface IDesktopNotifier
    {
        /// <summary>A scheduled or manual backup run finished.</summary>
        void NotifyBackupCompleted(string profileName, ProfileType type, RunOutcome outcome);

        /// <summary>A scheduled-task run finished.</summary>
        void NotifyTaskCompleted(string taskName, RunOutcome outcome);

        /// <summary>A registered USB device was connected.</summary>
        void NotifyDeviceConnected(string deviceName);

        /// <summary>A registered USB device was disconnected.</summary>
        void NotifyDeviceDisconnected(string deviceName);
    }
}
