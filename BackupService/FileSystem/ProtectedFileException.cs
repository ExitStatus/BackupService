namespace BackupService.FileSystem
{
    /// <summary>
    /// Thrown when a filesystem refuses to delete or overwrite a file it protects — a Google Docs/Sheets/Slides file
    /// (or shortcut) on Google Drive, which this app never creates, so one on Drive is always the user's own work.
    /// The sync engines keep the file and log a warning (<c>Kept '…' — {Reason}</c>) rather than an error.
    /// </summary>
    public sealed class ProtectedFileException(string message, string reason) : IOException(message)
    {
        public string Reason { get; } = reason;
    }
}
