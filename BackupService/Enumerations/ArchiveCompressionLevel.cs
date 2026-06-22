using System.ComponentModel;
using BackupService.Extensions;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How hard an <see cref="ProfileType.ArchiveSync"/> item compresses its ZIP. Maps to the BCL
    /// <see cref="System.IO.Compression.CompressionLevel"/> (see <c>ArchiveCompressionLevelExtensions</c>).
    /// </summary>
    public enum ArchiveCompressionLevel
    {
        [Description("Optimal")]
        [HelpText("Balance compression ratio against speed. The default.")]
        Optimal = 0,

        [Description("Fastest")]
        [HelpText("Compress quickly, accepting a larger archive.")]
        Fastest = 1,

        [Description("Smallest size")]
        [HelpText("Compress as much as possible; the slowest option, producing the smallest archive.")]
        SmallestSize = 2,

        [Description("No compression")]
        [HelpText("Store files without compressing them — fastest, but the largest archive.")]
        NoCompression = 3,
    }
}
