using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Authentication
{
    /// <summary>
    /// Records authentication events (login success/failure, password change) and
    /// reads them back as pages, newest first.
    /// </summary>
    public interface IAuthenticationHistoryService
    {
        Task RecordAsync(AuthenticationEventType eventType, CancellationToken cancellationToken = default);

        Task<PagedResult<AuthenticationHistory>> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    }
}
