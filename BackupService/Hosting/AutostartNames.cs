namespace BackupService.Hosting
{
    /// <summary>
    /// The name each environment's autostart entry goes by. The installed (Production) app keeps the plain name;
    /// any other environment gets its own, so running a development build — which reconciles its entry with its
    /// own database's option at startup — can never remove or repoint the installed app's entry. The two are
    /// already kept apart everywhere else (environment-suffixed mutex and stop event, separate data folders).
    /// </summary>
    public static class AutostartNames
    {
        /// <summary>The value under HKCU\…\Run.</summary>
        public static string WindowsValueName(string environmentName) =>
            IsProduction(environmentName) ? "BackupService" : $"BackupService ({environmentName})";

        /// <summary>The file in ~/.config/autostart.</summary>
        public static string LinuxDesktopFileName(string environmentName) =>
            IsProduction(environmentName) ? "backupservice.desktop" : $"backupservice-{environmentName.ToLowerInvariant()}.desktop";

        private static bool IsProduction(string environmentName) =>
            string.IsNullOrEmpty(environmentName) || string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
    }
}
