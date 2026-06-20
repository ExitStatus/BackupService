namespace BackupService.Dashboard
{
    /// <summary>
    /// Reads and aggregates backup run history into the <see cref="DashboardData"/> the dashboard renders.
    /// </summary>
    public interface IDashboardService
    {
        /// <summary>Aggregate the statistics and chart data for the last <paramref name="days"/> days.</summary>
        Task<DashboardData> GetAsync(int days, CancellationToken cancellationToken = default);
    }
}
