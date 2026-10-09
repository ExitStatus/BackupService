namespace BackupService.FileSystem
{
    /// <summary>
    /// Thrown by <see cref="IBackupFileSystem.OpenRead(string)"/> for a file that exists but can never be read as a
    /// stream of bytes — a Google Docs/Sheets/Slides file (or a shortcut) on Google Drive, which Drive can only export.
    /// The sync engines treat it as a non-fatal warning and skip the file, like a locked one (see
    /// <see cref="FileLock.IsSkippableReadError"/>); <see cref="Reason"/> is the text for that warning line.
    /// </summary>
    public sealed class FileNotDownloadableException(string message, string reason) : IOException(message)
    {
        public string Reason { get; } = reason;
    }
}
