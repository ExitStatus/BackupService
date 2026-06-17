using System.ComponentModel;

namespace BackupService.Enumerations
{
    public enum ProfileType
    {
        [Description("Folder Pairs")]
        FolderPair = 0,

        [Description("Instant Sync")]
        InstantSync = 1,
    }
}
