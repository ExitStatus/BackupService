namespace BackupService.FileSystem
{
    /// <summary>
    /// Browses the folders on the machine running the service (used by the admin UI's
    /// folder picker). Inaccessible directories are skipped rather than throwing.
    /// </summary>
    public interface IFolderBrowser
    {
        /// <summary>The available drive roots (e.g. <c>C:\</c>, <c>D:\</c>) with Explorer-style labels.</summary>
        IReadOnlyList<DriveEntry> GetDrives();

        /// <summary>"Quick access" shortcuts (Desktop, Documents, Downloads, Pictures) that exist.</summary>
        IReadOnlyList<FolderEntry> GetQuickAccess();

        /// <summary>Immediate subdirectories of <paramref name="path"/> (empty if unreadable).</summary>
        IReadOnlyList<FolderEntry> GetDirectories(string path);

        /// <summary>The parent directory of <paramref name="path"/>, or null at a root.</summary>
        string? GetParent(string path);
    }
}
