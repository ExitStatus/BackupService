namespace BackupService.FileSystem
{
    /// <summary>
    /// A drive shown under "This PC": its root path (e.g. <c>C:\</c>) and an Explorer-style
    /// label (e.g. <c>Windows (C:)</c> or <c>Local Disk (D:)</c>).
    /// </summary>
    public sealed record DriveEntry(string RootPath, string Label);
}
