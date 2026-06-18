using System.ComponentModel;
using BackupService.Extensions;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How an <see cref="ProfileType.ArchiveSync"/> item prunes its accumulated archives.
    /// </summary>
    public enum ArchiveRetentionMode
    {
        [Description("Keep last N archives")]
        [HelpText("Keep the newest N archives in the target folder; once there are more than N, the oldest are deleted after each run.")]
        KeepLastN = 0,

        [Description("Grandfather-father-son")]
        [HelpText("Tiered retention: keep N archives at each level. When a level overflows, its oldest archive is promoted up to the next level rather than deleted, so higher levels span exponentially longer periods. Beyond the top level the oldest is deleted.")]
        GrandfatherFatherSon = 1,
    }
}
