namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// Which files can carry an EXIF "date taken" — photos and camera raws. <see cref="MtpBackupFileSystem"/> only
    /// downloads a whole file to look for one when it can be there: a video (MP4, MOV, …) never has it, and
    /// downloading every video in full on every run just to find no date cost hours of USB transfer per plug-in.
    /// </summary>
    internal static class ExifFiles
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".tif", ".tiff", ".webp",
            ".arw", ".srf", ".sr2", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".orf", ".rw2", ".raf", ".dng",
            ".pef", ".srw", ".x3f", ".3fr", ".iiq", ".rwl", ".erf", ".kdc", ".mrw", ".mos",
        };

        public static bool MayHoldExif(string path)
        {
            var name = path.Replace('/', '\\');
            var dot = name.LastIndexOf('.');
            return dot > name.LastIndexOf('\\') && Extensions.Contains(name[dot..]);
        }
    }
}
