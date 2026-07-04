namespace BackupService.FileSystem
{
    public sealed class FolderBrowser : IFolderBrowser
    {
        // Linux mounts pseudo/virtual filesystems (proc, sysfs, tmpfs, cgroup, fuse.*, ...) that
        // DriveInfo.GetDrives() reports alongside real disks. Explorer has no equivalent, so filter
        // by DriveFormat to keep only filesystems a user would actually want to browse/back up.
        private static readonly HashSet<string> PseudoFileSystemFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "proc", "sysfs", "devtmpfs", "tmpfs", "devpts", "cgroup", "cgroup2", "mqueue", "overlay",
            "squashfs", "fusectl", "debugfs", "tracefs", "securityfs", "pstore", "bpf", "autofs",
            "binfmt_misc", "hugetlbfs", "configfs", "rpc_pipefs", "nsfs", "efivarfs", "ramfs", "sunrpc",
        };

        public IReadOnlyList<DriveEntry> GetDrives() =>
            DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && !IsPseudoFileSystem(drive))
                .Select(drive => new DriveEntry(drive.RootDirectory.FullName, DriveLabel(drive)))
                .ToList();

        private static bool IsPseudoFileSystem(DriveInfo drive)
        {
            try
            {
                return PseudoFileSystemFormats.Contains(drive.DriveFormat) || drive.DriveFormat.StartsWith("fuse.", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        public IReadOnlyList<FolderEntry> GetQuickAccess()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var folders = new[]
            {
                home,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(home, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };

            return folders
                .Where(path => !string.IsNullOrEmpty(path) && Directory.Exists(path))
                .Select(path => path == home ? ToEntry(path, "Home") : ToEntry(path))
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

        private static FolderEntry ToEntry(string path) =>
            ToEntry(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));

        private static FolderEntry ToEntry(string path, string displayName)
        {
            var name = displayName;
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
            if (!OperatingSystem.IsWindows())
            {
                // On Linux/macOS, DriveInfo.Name/VolumeLabel are just the mount path (e.g. "/"),
                // not a drive-letter/friendly-label pair, so show "<type> (<mount path>)" instead
                // of the Windows-shaped "<label> (<letter>)".
                return $"{DefaultLabel(drive)} ({drive.RootDirectory.FullName})";
            }

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
