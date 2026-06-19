namespace BackupService.FileSystem
{
    /// <summary>
    /// The outcome of building a ZIP from a directory: the entry names actually added, plus the files
    /// that had to be skipped because they could not be read (e.g. held open exclusively by another
    /// process). The caller logs and counts the skips — building is best-effort so one locked file
    /// never aborts the whole archive.
    /// </summary>
    public sealed record ZipBuildResult(IReadOnlyList<string> Added, IReadOnlyList<ZipSkippedFile> Skipped);

    /// <summary>A file omitted from an archive, with the reason its read failed.</summary>
    public sealed record ZipSkippedFile(string EntryName, string Reason);
}
