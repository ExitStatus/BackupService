using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Groups
{
    /// <summary>
    /// Application service for profile groups (mirrors <c>IConnectionService</c>).
    /// </summary>
    public interface IGroupService
    {
        /// <summary>Creates a new group and returns its id.</summary>
        Task<int> CreateAsync(string name, GroupConcurrency concurrency, CancellationToken cancellationToken = default);

        /// <summary>Updates an existing group. No-op if it doesn't exist.</summary>
        Task UpdateAsync(int id, string name, GroupConcurrency concurrency, CancellationToken cancellationToken = default);

        /// <summary>Reads a page of groups ordered by the given column/direction.</summary>
        Task<PagedResult<Group>> GetPageAsync(int pageNumber, int pageSize, GroupSortColumn sortColumn, bool descending, CancellationToken cancellationToken = default);

        /// <summary>Loads a single group, or null if not found.</summary>
        Task<Group?> GetAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>All groups as lightweight id+name+concurrency summaries (name-ascending), for pickers/labels.</summary>
        Task<IReadOnlyList<GroupSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Per-group member-profile aggregates (count, last-run-started, member ids), keyed by group id.
        /// Groups with no members are omitted.
        /// </summary>
        Task<IReadOnlyDictionary<int, GroupProfileStats>> GetProfileStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a group, ungrouping its member profiles (their <c>GroupId</c> becomes null). Returns the
        /// number of profiles that were ungrouped. No-op (returns 0) if the group doesn't exist.
        /// </summary>
        Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default);
    }
}
