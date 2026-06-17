namespace BackupService.Profiles
{
    /// <summary>
    /// An instant-sync item supplied when creating or updating a profile. <see cref="Id"/> is 0 for
    /// a new item, or the existing <c>InstantSyncItem.Id</c> when updating one.
    /// </summary>
    public sealed record InstantSyncInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        int DebounceMilliseconds,
        bool IncludeSubFolders,
        bool AllowDeletions);
}
