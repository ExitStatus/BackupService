using System.IO.Compression;
using BackupService.Enumerations;

namespace BackupService.Extensions
{
    /// <summary>
    /// Maps the project's <see cref="ArchiveCompressionLevel"/> (persisted, UI-friendly) to the BCL
    /// <see cref="CompressionLevel"/> the ZIP writer expects.
    /// </summary>
    public static class ArchiveCompressionLevelExtensions
    {
        public static CompressionLevel ToCompressionLevel(this ArchiveCompressionLevel level) => level switch
        {
            ArchiveCompressionLevel.Fastest => CompressionLevel.Fastest,
            ArchiveCompressionLevel.SmallestSize => CompressionLevel.SmallestSize,
            ArchiveCompressionLevel.NoCompression => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal,
        };
    }
}
