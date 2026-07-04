using System.Globalization;
using BackupService.Extensions;

namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Naming for database-backup archives: <c>database-backup_{yyyy-MM-dd_HHmmss}.zip</c> (local
    /// wall-clock at second precision, like ArchiveSync). Retention is derived from the target folder
    /// listing by parsing these names — a foreign/unparseable file is never treated as one of ours.
    /// Pure statics, unit-tested.
    /// </summary>
    public static class DatabaseBackupNaming
    {
        public const string Prefix = "database-backup";

        private const string TimestampFormat = "yyyy-MM-dd_HHmmss";

        public static string BuildFileName(DateTime localTimestamp) =>
            $"{Prefix}_{localTimestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.zip";

        /// <summary>Whether <paramref name="fileName"/> is one of our backups; yields its timestamp.</summary>
        public static bool TryParseTimestamp(string? fileName, out DateTime timestamp)
        {
            timestamp = default;
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            // Separator-agnostic: a remote (SMB/Google Drive) target lists backslash name-paths even on Linux, where
            // Path.GetFileName would not split them.
            var name = PathHelper.GetLeafName(fileName);
            if (!name.StartsWith(Prefix + "_", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var token = name[(Prefix.Length + 1)..^4];
            return DateTime.TryParseExact(token, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
        }

        /// <summary>
        /// The backup file names to delete so only the newest <paramref name="keep"/> remain — oldest
        /// first. Paths are reduced to their file names; files that aren't ours are left alone.
        /// </summary>
        public static IReadOnlyList<string> SelectExpired(IEnumerable<string> files, int keep)
        {
            keep = Math.Max(1, keep);

            var backups = new List<(string Name, DateTime Timestamp)>();
            foreach (var file in files)
            {
                var name = PathHelper.GetLeafName(file);
                if (TryParseTimestamp(name, out var timestamp))
                {
                    backups.Add((name, timestamp));
                }
            }

            return backups
                .OrderByDescending(b => b.Timestamp)
                .Skip(keep)
                .OrderBy(b => b.Timestamp)
                .Select(b => b.Name)
                .ToList();
        }
    }
}
