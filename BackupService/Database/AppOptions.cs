namespace BackupService.Database
{
    /// <summary>
    /// Single-row application options edited from the Settings → Options panel (one row, like
    /// <see cref="LogRetentionSettings"/>; seeded lazily with the defaults below). All three are
    /// Windows-desktop integrations that are no-ops off Windows.
    /// </summary>
    public class AppOptions
    {
        public int Id { get; set; }

        /// <summary>Launch the app (in <c>-background</c> mode) when the current user signs in to Windows.</summary>
        public bool StartWithWindows { get; set; }

        /// <summary>Show a system-tray (notification-area) icon while the app is running.</summary>
        public bool ShowTrayIcon { get; set; }

        /// <summary>Show a Windows tray balloon when a discrete backup or scheduled-task run completes.</summary>
        public bool AllowNotifications { get; set; }
    }
}
