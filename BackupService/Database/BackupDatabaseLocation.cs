using Microsoft.Extensions.Hosting.WindowsServices;

namespace BackupService.Database
{
    /// <summary>
    /// Resolves where the single SQLite database file lives.
    ///
    /// When deployed as a Windows Service the database is shared by the whole
    /// machine and lives under ProgramData (independent of the service account),
    /// so there is only ever one database for a deployed application. When run
    /// interactively (debugging from the console) it sits next to the app's exe.
    /// </summary>
    public static class BackupDatabaseLocation
    {
        private const string FileName = "backupservice.db";
        private const string ProgramDataFolder = "BackupService";

        /// <summary>
        /// The directory that holds the app's persistent data (the SQLite database and the data-protection
        /// key ring). ProgramData when running as a Windows Service (shared machine-wide); next to the exe
        /// otherwise. Created if missing.
        /// </summary>
        public static string GetDataDirectory()
        {
            string directory = WindowsServiceHelpers.IsWindowsService()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    ProgramDataFolder)
                : AppContext.BaseDirectory;

            // Callers (SQLite, the key ring) won't create missing directories; ensure it exists.
            Directory.CreateDirectory(directory);

            return directory;
        }

        public static string GetDatabasePath() => Path.Combine(GetDataDirectory(), FileName);

        public static string GetConnectionString()
            => $"Data Source={GetDatabasePath()}";
    }
}
