namespace BackupService.FileSystem
{
    /// <summary>
    /// A folder shown in the browser: its full path, display name, and last-write time
    /// (null when the timestamp could not be read).
    /// </summary>
    public sealed record FolderEntry(string FullPath, string Name, DateTimeOffset? DateModified);
}
