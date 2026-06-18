namespace BackupService.FileSystem
{
    /// <summary>
    /// Abstraction over the file operations a backup needs, so the sync engine
    /// (<see cref="Scheduling.FolderPairSynchronizer"/>) can be unit-tested against a fake. The real
    /// implementation (<see cref="BackupFileSystem"/>) is a straight pass-through to
    /// <see cref="System.IO"/>; each member throws on an IO error so the caller can log it. Only
    /// primitives live here — the crash-safe copy-through-temp orchestration is in the synchroniser
    /// so its cleanup-on-error can be tested without touching disk.
    /// </summary>
    public interface IBackupFileSystem
    {
        bool DirectoryExists(string path);

        void CreateDirectory(string path);

        void DeleteDirectory(string path, bool recursive);

        bool FileExists(string path);

        /// <summary>Full paths of the files directly in <paramref name="directory"/> (not recursive).</summary>
        IReadOnlyList<string> GetFiles(string directory);

        /// <summary>Full paths of the sub-directories directly in <paramref name="directory"/> (not recursive).</summary>
        IReadOnlyList<string> GetDirectories(string directory);

        DateTime GetLastWriteTimeUtc(string path);

        void SetLastWriteTimeUtc(string path, DateTime value);

        void CopyFile(string source, string destination, bool overwrite);

        /// <summary>Renames/moves <paramref name="source"/> to <paramref name="destination"/>.</summary>
        void MoveFile(string source, string destination, bool overwrite);

        void DeleteFile(string path);

        /// <summary>True if both files have identical content (compared by length, then bytes).</summary>
        bool FilesContentEqual(string a, string b);

        /// <summary>
        /// Returns a unique path under a local temp area for <paramref name="fileName"/> (creating its
        /// containing directory). Used to build an archive locally before copying it to the target.
        /// </summary>
        string GetTempFilePath(string fileName);

        /// <summary>
        /// Creates a ZIP of <paramref name="sourceDirectory"/> at <paramref name="destinationZip"/>;
        /// when <paramref name="includeSubfolders"/> is false only the top-level files are included.
        /// Returns the relative entry names added to the archive (for verbose logging).
        /// </summary>
        IReadOnlyList<string> CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders);
    }
}
