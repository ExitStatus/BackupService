using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// Application service for backup profiles.
    /// </summary>
    public interface IProfileService
    {
        /// <summary>
        /// Creates a new profile of the given type with its type-specific items. Pass the
        /// <paramref name="folderPairs"/> for a FolderPair profile, the
        /// <paramref name="instantSyncItems"/> for an InstantSync profile, or the
        /// <paramref name="archiveSyncItems"/> for an ArchiveSync profile; the others stay empty.
        /// </summary>
        Task CreateAsync(
            string name,
            string? description,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            IReadOnlyList<InstantSyncInput>? instantSyncItems = null,
            IReadOnlyList<ArchiveSyncInput>? archiveSyncItems = null,
            IReadOnlyList<LightroomArchiveInput>? lightroomArchiveItems = null,
            string? lightroomFolder = null,
            string? rawFormats = null,
            string? rawFolderName = null,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a page of profiles ordered by the given column/direction.
        /// </summary>
        Task<PagedResult<Profile>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ProfileSortColumn sortColumn,
            bool descending,
            ProfileType? type = null,
            string? filter = null,
            bool? enabled = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads a single profile (with its folder pairs) for editing, or null if not found.
        /// </summary>
        Task<Profile?> GetAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>
        /// All profiles as lightweight id+name summaries (name-ascending), for pickers/filters.
        /// </summary>
        Task<IReadOnlyList<ProfileSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// The number of profiles of each <see cref="ProfileType"/> (types with none are omitted). Used by the
        /// Profiles page to decide which "Arrange by Type" tabs to show.
        /// </summary>
        Task<IReadOnlyDictionary<ProfileType, int>> GetCountsByTypeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a profile (and its folder pairs, via cascade). No-op if it doesn't exist.
        /// </summary>
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets just the <see cref="Profile.Enabled"/> flag (used by the inline grid toggle).
        /// No-op if the profile doesn't exist.
        /// </summary>
        Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing profile and syncs its type-specific items (adds new ones, updates
        /// matched ones by id, removes the rest). The profile type is fixed and not changed, so only
        /// the list matching the profile's type is used; the other should be empty.
        /// </summary>
        Task UpdateAsync(
            int id,
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            IReadOnlyList<InstantSyncInput>? instantSyncItems = null,
            IReadOnlyList<ArchiveSyncInput>? archiveSyncItems = null,
            IReadOnlyList<LightroomArchiveInput>? lightroomArchiveItems = null,
            string? lightroomFolder = null,
            string? rawFormats = null,
            string? rawFolderName = null,
            bool handleMissedSync = false,
            CancellationToken cancellationToken = default);
    }
}
