namespace BackupService.FileSystem
{
    /// <summary>
    /// A file's last-write-time and size, read together by <see cref="IBackupFileSystem.GetFileStat"/> so the sync
    /// engine's per-file compare costs one metadata lookup per side (a separate round trip each on SMB/MTP).
    /// <see cref="Size"/> is what the filesystem reports; a value of zero or less means "not known" to callers that
    /// verify copies against it (an MTP device can report 0, or a sentinel that overflows to -1, for a real file).
    /// </summary>
    public readonly record struct FileStat(DateTime LastWriteTimeUtc, long Size);
}
