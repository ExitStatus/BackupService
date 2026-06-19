using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// What a backup filter rule's pattern is matched against. Matching is name-only and
    /// case-insensitive (see <see cref="FileSystem.BackupFilter"/>).
    /// </summary>
    public enum FilterKind
    {
        /// <summary>A file-name pattern (wildcards allowed), matched against each file's name.</summary>
        [Description("File")]
        File = 0,

        /// <summary>A folder-name pattern, matched against folder names; a match skips that subtree.</summary>
        [Description("Folder")]
        Folder = 1,
    }
}
