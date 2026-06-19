using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>Which list a backup filter rule belongs to.</summary>
    public enum FilterDirection
    {
        /// <summary>Restricts the backup to matching files (an empty include list means "all files").</summary>
        [Description("Include")]
        Include = 0,

        /// <summary>Removes matching files/folders from the backup.</summary>
        [Description("Exclude")]
        Exclude = 1,
    }
}
