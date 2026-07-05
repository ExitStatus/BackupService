using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// A two-way sync item supplied when creating or updating a profile. <see cref="Id"/> is 0 for a new
    /// item, or the existing <c>TwoWaySyncItem.Id</c> when updating one.
    /// </summary>
    public sealed record TwoWaySyncInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        bool IncludeSubFolders,
        ConflictResolution ConflictResolution,
        bool PropagateDeletions,
        IReadOnlyList<FilterInput>? Filters = null);
}
