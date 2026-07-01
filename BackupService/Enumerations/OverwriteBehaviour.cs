using System.ComponentModel;
using BackupService.Extensions;

namespace BackupService.Enumerations
{
    public enum OverwriteBehaviour
    {
        [Description("Do not overwrite newer files")]
        [HelpText("Any file at the destination with a newer date will not be overwritten")]
        DoNotOverwriteNewer = 0,

        [Description("Update only if content match")]
        [HelpText("If a file on the destination is newer, compare the content and if it matches update the date stamp of the destination file to match the source file")]
        UpdateOnlyIfContentMatches = 1,

        [Description("Always overwrite target files")]
        [HelpText("Always overwrite files at the destination regardless of if they are newer or not")]
        AlwaysOverwrite = 2,
    }
}
