using BackupService.Connections;

namespace BackupService.Dashboard
{
    /// <summary>
    /// Default <see cref="IStorageUsageService"/>. Enumerates local + mapped drives via
    /// <see cref="DriveInfo"/> and queries each connection's space via <see cref="IConnectionSpaceService"/>
    /// (in parallel, best-effort). Unreachable connections, unlimited-quota connections (no finite capacity
    /// to plot) and inaccessible drives are left out.
    /// </summary>
    public sealed class StorageUsageService(
        IConnectionService connectionService,
        IConnectionSpaceService spaceService,
        ILogger<StorageUsageService> logger) : IStorageUsageService
    {
        public async Task<IReadOnlyList<StorageUsage>> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            var result = new List<StorageUsage>();
            result.AddRange(GetLocalDrives());

            IReadOnlyList<ConnectionSummary> connections;
            try
            {
                connections = await connectionService.GetSummariesAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not list connections for the storage-usage chart.");
                return result;
            }

            // Query each connection's space in parallel (best-effort); include only reachable ones that report a
            // finite capacity (an unlimited-quota Drive has no capacity to plot).
            var spaces = await Task.WhenAll(connections.Select(async c =>
            {
                var space = await spaceService.GetSpaceAsync(c.Id, cancellationToken);
                if (space is { Unlimited: false, TotalBytes: > 0, FreeBytes: not null })
                {
                    var total = space.TotalBytes.Value;
                    return new StorageUsage(c.Name, total, Math.Clamp(space.FreeBytes.Value, 0, total));
                }
                return null;
            }));

            result.AddRange(spaces.Where(s => s is not null).Select(s => s!));
            return result;
        }

        // Local (fixed) + mapped (network) drives that are ready and report a capacity.
        private List<StorageUsage> GetLocalDrives()
        {
            var drives = new List<StorageUsage>();

            DriveInfo[] all;
            try
            {
                all = DriveInfo.GetDrives();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enumerate local drives for the storage-usage chart.");
                return drives;
            }

            foreach (var drive in all)
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Network) || drive.TotalSize <= 0)
                    {
                        continue;
                    }

                    drives.Add(new StorageUsage(DriveName(drive), drive.TotalSize, Math.Clamp(drive.AvailableFreeSpace, 0, drive.TotalSize)));
                }
                catch
                {
                    // A drive that throws on access (permissions, disconnected mapping) — skip it.
                }
            }

            return drives;
        }

        // "Windows (C:)" style label, falling back to just the root when there's no volume label.
        private static string DriveName(DriveInfo drive)
        {
            var letter = drive.Name.TrimEnd('\\', '/');
            try
            {
                if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                {
                    return $"{drive.VolumeLabel} ({letter})";
                }
            }
            catch
            {
                // VolumeLabel can throw for some drives — fall back to the letter.
            }

            return letter;
        }
    }
}
