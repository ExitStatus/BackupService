using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BackupService.Hosting
{
    /// <summary>
    /// Windows <see cref="IStartupManager"/>. Adds/removes a per-user autostart entry under
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> whose command launches the published exe in
    /// <c>-background</c> mode. Per-user (HKCU, not HKLM) so it needs no elevation and matches the per-user
    /// data model. Re-applying rewrites the command, so the registered exe path tracks the current location
    /// after a redeploy.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsStartupManager(ILogger<WindowsStartupManager> logger, IHostEnvironment environment) : IStartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Per environment, so a development run never touches the installed app's entry (see AutostartNames).
        private readonly string _valueName = AutostartNames.WindowsValueName(environment.EnvironmentName);

        public void Apply(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null)
                {
                    return;
                }

                if (enabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath))
                    {
                        return;
                    }

                    key.SetValue(_valueName, $"\"{exePath}\" -background");
                }
                else
                {
                    key.DeleteValue(_valueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to {Action} the Windows autostart entry.", enabled ? "register" : "remove");
            }
        }

        public bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(_valueName) is not null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read the Windows autostart entry.");
                return false;
            }
        }
    }
}
