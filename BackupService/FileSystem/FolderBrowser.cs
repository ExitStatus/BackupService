namespace BackupService.FileSystem
{
    public sealed class FolderBrowser : IFolderBrowser
    {
        public IReadOnlyList<string> GetRoots() =>
            DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToList();

        public IReadOnlyList<string> GetDirectories(string path)
        {
            try
            {
                return Directory.GetDirectories(path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
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
    }
}
