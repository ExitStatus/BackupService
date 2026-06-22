using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// An archive-sync item supplied when creating or updating a profile. <see cref="Id"/> is 0 for
    /// a new item, or the existing <c>ArchiveSyncItem.Id</c> when updating one.
    /// </summary>
    public sealed record ArchiveSyncInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        string FileName,
        bool IncludeSubFolders,
        bool OnlyCopyOnChange,
        ArchiveRetentionMode RetentionMode,
        int RetentionCount,
        int MaxLevels,
        IReadOnlyList<FilterInput>? Filters = null,
        int? SourceConnectionId = null,
        int? TargetConnectionId = null);
}
