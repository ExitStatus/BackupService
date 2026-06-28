namespace BackupService.Database
{
    /// <summary>
    /// Resolves where the single SQLite database file (and the app's other per-user data — the data-protection key
    /// ring, the logs folder and the PID file) lives.
    ///
    /// A deployed run stores everything <b>per user</b> under <c>%LOCALAPPDATA%\BackupService</c>, so several Windows
    /// users can each run their own isolated configuration. A developer run (the <c>Development</c> environment) keeps
    /// the data next to the build output (<c>AppContext.BaseDirectory</c>, i.e. <c>bin\…</c>) so the dev workflow and
    /// WAL inspection are unchanged.
    /// </summary>
    public static class BackupDatabaseLocation
    {
        private const string FileName = "backupservice.db";
        private const string DataFolder = "BackupService";

        /// <summary>
        /// The directory that holds the app's persistent per-user data. <c>%LOCALAPPDATA%\BackupService</c> for a
        /// deployed run; next to the build output in Development. Created if missing.
        /// </summary>
        public static string GetDataDirectory()
        {
            // Read the environment directly: this runs before the host (and its configuration) is built.
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

            string directory = isDevelopment
                ? AppContext.BaseDirectory
                // LocalApplicationData (not Roaming) — a SQLite database should never roam to another machine.
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    DataFolder);

            // Callers (SQLite, the key ring) won't create missing directories; ensure it exists.
            Directory.CreateDirectory(directory);

            return directory;
        }

        public static string GetDatabasePath() => Path.Combine(GetDataDirectory(), FileName);

        public static string GetConnectionString()
            => $"Data Source={GetDatabasePath()}";
    }
}
