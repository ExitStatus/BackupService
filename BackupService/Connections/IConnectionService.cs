using BackupService.Connections.GoogleDrive;
using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// CRUD for remote-resource connections (SMB, Google Drive, etc.). Mirrors <c>IProfileService</c>: a
    /// short-lived DbContext per call, an operation log per mutation, and the type fixed after creation.
    /// </summary>
    public interface IConnectionService
    {
        Task<int> CreateAsync(string name, ConnectionType type, SmbConnectionInput smb, CancellationToken cancellationToken = default);

        Task UpdateAsync(int id, string name, SmbConnectionInput smb, CancellationToken cancellationToken = default);

        Task<int> CreateAsync(string name, ConnectionType type, GoogleDriveConnectionInput googleDrive, CancellationToken cancellationToken = default);

        Task UpdateAsync(int id, string name, GoogleDriveConnectionInput googleDrive, CancellationToken cancellationToken = default);

        Task<int> CreateAsync(string name, ConnectionType type, UsbConnectionInput usb, CancellationToken cancellationToken = default);

        Task UpdateAsync(int id, string name, UsbConnectionInput usb, CancellationToken cancellationToken = default);

        Task<PagedResult<Connection>> GetPageAsync(int pageNumber, int pageSize, ConnectionSortColumn sortColumn, bool descending, CancellationToken cancellationToken = default);

        Task<Connection?> GetAsync(int id, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<ConnectionSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns, keyed by connection id, the number of distinct profiles that reference each connection as a
        /// source or target (across every backup-entry type). Connections with no references are omitted.
        /// </summary>
        Task<IReadOnlyDictionary<int, int>> GetProfileUsageCountsAsync(CancellationToken cancellationToken = default);

        Task<ConnectionDeleteResult> DeleteAsync(int id, CancellationToken cancellationToken = default);
    }
}
