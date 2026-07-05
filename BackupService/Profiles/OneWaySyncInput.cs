using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// A one way sync supplied when creating or updating a profile. <see cref="Id"/> is 0 for
    /// a new pair, or the existing <c>OneWaySyncItem.Id</c> when updating one.
    /// </summary>
    public sealed record OneWaySyncInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        bool AllowDeletions,
        bool IncludeSubFolders,
        OverwriteBehaviour OverwriteBehaviour,
        IReadOnlyList<FilterInput>? Filters = null);
}
