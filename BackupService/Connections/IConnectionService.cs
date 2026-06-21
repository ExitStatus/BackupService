using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// CRUD for remote-resource connections (SMB, etc.). Mirrors <c>IProfileService</c>: a short-lived
    /// DbContext per call, an operation log per mutation, and the type fixed after creation.
    /// </summary>
    public interface IConnectionService
    {
        Task<int> CreateAsync(string name, ConnectionType type, SmbConnectionInput smb, CancellationToken cancellationToken = default);

        Task UpdateAsync(int id, string name, SmbConnectionInput smb, CancellationToken cancellationToken = default);

        Task<PagedResult<Connection>> GetPageAsync(int pageNumber, int pageSize, ConnectionSortColumn sortColumn, bool descending, CancellationToken cancellationToken = default);

        Task<Connection?> GetAsync(int id, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ConnectionSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);

        Task<ConnectionDeleteResult> DeleteAsync(int id, CancellationToken cancellationToken = default);
    }
}
