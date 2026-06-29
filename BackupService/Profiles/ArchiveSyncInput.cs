using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// An archive-sync item supplied when creating or updating a profile. <see cref="Id"/> is 0 for
    /// a new item, or the existing <c>ArchiveSyncItem.Id</c> when updating one. <see cref="Password"/> is
    /// plaintext and encrypted at rest by the service layer; null/blank on an update keeps the stored one.
    /// </summary>
    public sealed record ArchiveSyncInput(
        int Id,
        string Name,
        string SourceFolder,
        string TargetFolder,
        string FileName,
        bool IncludeSubFolders,
        bool OnlyCopyOnChange,
        ArchiveCompressionLevel CompressionLevel,
        bool PasswordProtect,
        string? Password,
        ArchiveEncryptionMethod EncryptionMethod,
        ArchiveRetentionMode RetentionMode,
        int RetentionCount,
        int MaxLevels,
        IReadOnlyList<FilterInput>? Filters = null);
}
