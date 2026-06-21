using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// A folder pair supplied when creating or updating a profile. <see cref="Id"/> is 0 for
    /// a new pair, or the existing <c>FolderPair.Id</c> when updating one.
    /// </summary>
    public sealed record FolderPairInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        bool AllowDeletions,
        bool IncludeSubFolders,
        OverwriteBehaviour OverwriteBehaviour,
        IReadOnlyList<FilterInput>? Filters = null,
        int? SourceConnectionId = null,
        int? TargetConnectionId = null);
}
