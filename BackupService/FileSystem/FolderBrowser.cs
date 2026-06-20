namespace BackupService.FileSystem
{
    public sealed class FolderBrowser : IFolderBrowser
    {
        public IReadOnlyList<DriveEntry> GetDrives() =>
            DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new DriveEntry(drive.RootDirectory.FullName, DriveLabel(drive)))
                .ToList();

        public IReadOnlyList<FolderEntry> GetQuickAccess()
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };

            return folders
                .Where(path => !string.IsNullOrEmpty(path) && Directory.Exists(path))
                .Select(ToEntry)
                .ToList();
        }

        public IReadOnlyList<FolderEntry> GetDirectories(string path)
        {
            try
            {
                return Directory.GetDirectories(path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(ToEntry)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                return [];
            }
        }

        public string? GetParent(string path)
        {
            try
            {
                return Directory.GetParent(path)?.FullName;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                return null;
            }
        }

        public string CreateDirectory(string parentPath, string name)
        {
            var full = Path.Combine(parentPath, name);
            Directory.CreateDirectory(full);
            return full;
        }

        private static FolderEntry ToEntry(string path)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            DateTimeOffset? modified = null;
            try
            {
                modified = Directory.GetLastWriteTime(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            {
                // Leave the timestamp blank when it cannot be read.
            }

            return new FolderEntry(path, string.IsNullOrEmpty(name) ? path : name, modified);
        }

        private static string DriveLabel(DriveInfo drive)
        {
            // Explorer shows "<Volume label> (C:)", falling back to "Local Disk (C:)".
            var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar); // "C:"
            string name;
            try
            {
                name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? DefaultLabel(drive) : drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                name = DefaultLabel(drive);
            }

            return $"{name} ({letter})";
        }

        private static string DefaultLabel(DriveInfo drive) => drive.DriveType switch
        {
            DriveType.Network => "Network Drive",
            DriveType.Removable => "Removable Disk",
            DriveType.CDRom => "CD Drive",
            _ => "Local Disk",
        };
    }
}
