namespace BackupService.Dashboard
{
    /// <summary>
    /// A single storage device or connection for the dashboard's space-usage chart: its display
    /// <paramref name="Name"/> and its capacity/free figures in bytes. Covers local + mapped drives and any
    /// currently-connected connection with a finite capacity.
    /// </summary>
    public sealed record StorageUsage(string Name, long TotalBytes, long FreeBytes)
    {
        /// <summary>Used space (never negative).</summary>
        public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

        /// <summary>Free space as a percentage of capacity (0 when the capacity is unknown).</summary>
        public double FreePercent => TotalBytes > 0 ? (double)FreeBytes / TotalBytes * 100.0 : 0.0;
    }
}
