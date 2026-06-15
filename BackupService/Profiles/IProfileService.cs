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
        /// Creates a new profile of the given type with a single folder pair.
        /// </summary>
        Task CreateAsync(
            string name,
            string? description,
            ProfileType type,
            string sourceFolder,
            string targetFolder,
            bool watchFolder,
            string? scheduleCron,
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
        /// Deletes a profile (and its folder pairs, via cascade). No-op if it doesn't exist.
        /// </summary>
        Task DeleteAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing profile and its single folder pair. The profile type is fixed
        /// and is not changed.
        /// </summary>
        Task UpdateAsync(
            int id,
            string name,
            string? description,
            string sourceFolder,
            string targetFolder,
            bool watchFolder,
            string? scheduleCron,
            CancellationToken cancellationToken = default);
    }
}
