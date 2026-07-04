using System.Runtime.Versioning;

namespace BackupService.Hosting
{
    /// <summary>
    /// Linux <see cref="IStartupManager"/>. Adds/removes an XDG autostart entry at
    /// <c>~/.config/autostart/backupservice.desktop</c> so the app launches when the current user logs into a
    /// desktop session — the Linux analogue of <see cref="WindowsStartupManager"/>'s HKCU Run-key entry. The
    /// <c>-background</c>/<c>-stop</c> process-control model is Windows-only (see
    /// <see cref="BackgroundProcessManager"/>), so <c>Exec=</c> launches the plain foreground exe with no
    /// arguments; the desktop session keeps it running detached from any terminal. Re-applying rewrites the
    /// file, so the registered exe path tracks the current location after a redeploy.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class LinuxStartupManager(ILogger<LinuxStartupManager> logger) : IStartupManager
    {
        private const string DesktopFileName = "backupservice.desktop";

        private static string AutostartDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");

        private static string DesktopFilePath => Path.Combine(AutostartDirectory, DesktopFileName);

        public void Apply(bool enabled)
        {
            try
            {
                if (enabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath))
                    {
                        return;
                    }

                    Directory.CreateDirectory(AutostartDirectory);
                    var workingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                    File.WriteAllText(DesktopFilePath, LinuxAutostartEntry.Build(exePath, workingDirectory));
                }
                else if (File.Exists(DesktopFilePath))
                {
                    File.Delete(DesktopFilePath);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to {Action} the Linux autostart entry.", enabled ? "register" : "remove");
            }
        }

        public bool IsEnabled()
        {
            try
            {
                return File.Exists(DesktopFilePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read the Linux autostart entry.");
                return false;
            }
        }
    }
}
