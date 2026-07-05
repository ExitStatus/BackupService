using System.ComponentModel;
using BackupService.Extensions;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How a <see cref="ProfileType.TwoWaySync"/> item resolves a genuine conflict — the same file changed on
    /// <b>both</b> sides since the last sync (including a modify-vs-delete). Chosen per two-way sync item.
    /// </summary>
    public enum ConflictResolution
    {
        [Description("Newer wins")]
        [HelpText("Keep the more recently modified version and copy it over the older side. (A modified file wins over a deletion.)")]
        NewerWins = 0,

        [Description("Source wins")]
        [HelpText("The source (left) side always wins a conflict, overwriting the target side.")]
        SourceWins = 1,

        [Description("Target wins")]
        [HelpText("The target (right) side always wins a conflict, overwriting the source side.")]
        TargetWins = 2,

        [Description("Keep both")]
        [HelpText("Keep both versions: one side's copy is renamed (e.g. 'report (conflict 2026-07-04 100502).docx') so neither edit is lost, and both end up on both sides.")]
        KeepBoth = 3,

        [Description("Skip & log")]
        [HelpText("Leave both sides untouched and record a warning in the run log for manual resolution.")]
        Skip = 4,
    }
}
