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

        /// <summary>
        /// TEMPORARY one-shot migration: the Windows-service build kept the database machine-wide under
        /// <c>%ProgramData%\BackupService</c>. On the first deployed run after switching to the per-user model, if the
        /// per-user database doesn't exist yet but the old machine-wide one does, move it (and its <c>-wal</c>/<c>-shm</c>
        /// sidecars) into the current user's data directory. Best effort and idempotent — once moved, the legacy file
        /// is gone, so only the first user to start claims it and later users start with a fresh database.
        ///
        /// Safe to delete once every deployment has migrated.
        /// </summary>
        public static void MigrateFromLegacyLocationIfNeeded()
        {
            // Development keeps its database next to the build output; there is nothing to migrate.
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var targetDirectory = GetDataDirectory();
                var targetDatabase = Path.Combine(targetDirectory, FileName);
                if (File.Exists(targetDatabase))
                {
                    return; // Already have a per-user database — nothing to do.
                }

                var legacyDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    DataFolder);
                var legacyDatabase = Path.Combine(legacyDirectory, FileName);
                if (!File.Exists(legacyDatabase))
                {
                    return; // No legacy database to bring across.
                }

                // Move the database and its WAL/SHM sidecars (any open handle would be on the old service, now stopped).
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var source = legacyDatabase + suffix;
                    if (File.Exists(source))
                    {
                        File.Move(source, targetDatabase + suffix, overwrite: false);
                    }
                }

                Console.WriteLine($"Migrated database from '{legacyDirectory}' to '{targetDirectory}'.");
            }
            catch (Exception ex)
            {
                // Never block startup on the migration — a fresh database is created if this fails.
                Console.Error.WriteLine($"Database migration from the legacy location failed: {ex.Message}");
            }
        }
    }
}
