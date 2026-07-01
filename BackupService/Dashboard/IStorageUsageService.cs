namespace BackupService.Dashboard
{
    /// <summary>
    /// Gathers the live storage picture for the dashboard's space-usage chart: every ready local (fixed) and
    /// mapped (network) drive, plus each connection that is currently reachable and reports a finite capacity.
    /// Best-effort — an unreachable connection or an inaccessible drive is simply omitted, so the set naturally
    /// grows and shrinks as devices/connections come and go.
    /// </summary>
    public interface IStorageUsageService
    {
        Task<IReadOnlyList<StorageUsage>> GetUsageAsync(CancellationToken cancellationToken = default);
    }
}
