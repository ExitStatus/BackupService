namespace BackupService.Hosting
{
    /// <summary>
    /// Builds the XDG Desktop Entry content for <see cref="LinuxStartupManager"/>'s autostart file. Pure and not
    /// OS-gated (unlike <see cref="LinuxStartupManager"/> itself) so it can be unit-tested on any platform.
    /// </summary>
    internal static class LinuxAutostartEntry
    {
        public static string Build(string exePath, string workingDirectory) =>
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Backup Service\n" +
            "Comment=Starts Backup Service automatically when you log in\n" +
            $"Exec=\"{exePath}\"\n" +
            $"Path={workingDirectory}\n" +
            "Terminal=false\n" +
            "NoDisplay=true\n" +
            "X-GNOME-Autostart-enabled=true\n";
    }
}
