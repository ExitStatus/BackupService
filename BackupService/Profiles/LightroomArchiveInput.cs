namespace BackupService.Profiles
{
    /// <summary>
    /// A lightroom-archive item supplied when creating or updating a profile. <see cref="Id"/> is 0 for a
    /// new item, or the existing <c>LightroomArchiveItem.Id</c> when updating one. The source is always local;
    /// the target connection (if any) is profile-level, not per item.
    /// </summary>
    public sealed record LightroomArchiveInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        int DebounceMilliseconds,
        bool IncludeSubFolders,
        bool AllowDeletions);
}
