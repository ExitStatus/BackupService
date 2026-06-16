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
        /// Creates a new profile of the given type with one or more folder pairs.
        /// </summary>
        Task CreateAsync(
            string name,
            string? description,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a page of profiles ordered by the given column/direction.
        /// </summary>
        Task<PagedResult<Profile>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ProfileSortColumn sortColumn,
            bool descending,
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
        /// Deletes a profile (and its folder pairs, via cascade). No-op if it doesn't exist.
        /// </summary>
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets just the <see cref="Profile.Enabled"/> flag (used by the inline grid toggle).
        /// No-op if the profile doesn't exist.
        /// </summary>
        Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing profile and syncs its folder pairs (adds new ones, updates
        /// matched ones by id, removes the rest). The profile type is fixed and not changed.
        /// </summary>
        Task UpdateAsync(
            int id,
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken = default);
    }
}
