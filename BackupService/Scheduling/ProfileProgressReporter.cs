using BackupService.Profiles;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Accumulates per-file progress reports for one run and pushes the resulting percentage (0–100) to
    /// <see cref="IProfileStatusService.SetProgress"/>. A run reports single-threaded, but the running count
    /// is interlocked to be safe. <paramref name="total"/> of 0 (unknown/empty) reports 100%.
    /// </summary>
    internal sealed class ProfileProgressReporter(IProfileStatusService statusService, int profileId, int total) : IProgress<int>
    {
        private int _processed;

        public void Report(int value)
        {
            var processed = Interlocked.Add(ref _processed, value);
            var percent = total > 0 ? (int)(processed * 100L / total) : 100;
            statusService.SetProgress(profileId, percent);
        }
    }
}
