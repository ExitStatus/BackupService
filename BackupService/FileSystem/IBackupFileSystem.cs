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

        /// <summary>The size of <paramref name="path"/> in bytes.</summary>
        long GetFileSize(string path);

        void SetLastWriteTimeUtc(string path, DateTime value);

        /// <summary>
        /// Opens <paramref name="path"/> for reading. Used for cross-filesystem copies (read from one
        /// filesystem, write to another) — see the synchroniser. The caller disposes the stream.
        /// </summary>
        Stream OpenRead(string path);

        /// <summary>
        /// Creates (or truncates) <paramref name="path"/> and opens it for writing. The caller disposes
        /// the stream. The containing directory is expected to exist.
        /// </summary>
        Stream OpenWrite(string path);

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
        /// <paramref name="includeEntry"/> (relative entry path → keep?) optionally filters which files
        /// are archived — a file for which it returns false is simply omitted (not added, not skipped).
        /// Building is otherwise best-effort: a file that can't be read (e.g. locked by another process)
        /// is skipped rather than aborting the archive. Returns the relative entry names added (for
        /// verbose logging) plus the files skipped and why, so the caller can log/count them.
        /// </summary>
        ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null);
    }
}
