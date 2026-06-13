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

        public static string GetDatabasePath()
        {
            string directory = WindowsServiceHelpers.IsWindowsService()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    ProgramDataFolder)
                : AppContext.BaseDirectory;

            // SQLite will not create missing directories; ensure it exists.
            Directory.CreateDirectory(directory);

            return Path.Combine(directory, FileName);
        }

        public static string GetConnectionString()
            => $"Data Source={GetDatabasePath()}";
    }
}
