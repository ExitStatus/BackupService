using BackupService.Profiles;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Accumulates per-file progress for a multi-step run (one step per folder pair / sync item) and pushes a
    /// <see cref="ProfileProgress"/> to <see cref="IProfileStatusService.SetProgress(int, ProfileProgress)"/>:
    /// the current step's own percent plus the overall percent across every step. The handler calls
    /// <see cref="BeginStep"/> before processing each step; the synchroniser then reports 1 per in-scope file
    /// handled. A run reports single-threaded, but the counters are interlocked to be safe. A step (or the
    /// whole run) with 0 counted files reports 100%.
    /// </summary>
    internal sealed class ProfileProgressReporter(
        IProfileStatusService statusService, int profileId, IReadOnlyList<(string Name, int Count)> steps) : IProgress<int>
    {
        private readonly long _totalFiles = steps.Sum(s => (long)s.Count);
        private int _currentStep = -1;
        private long _totalProcessed;
        private long _stepProcessed;

        /// <summary>Switches to the given step (0-based) and reports its starting percent.</summary>
        public void BeginStep(int index)
        {
            _currentStep = index;
            Interlocked.Exchange(ref _stepProcessed, 0);
            Push(0, Interlocked.Read(ref _totalProcessed));
        }

        public void Report(int value)
        {
            var step = Interlocked.Add(ref _stepProcessed, value);
            var total = Interlocked.Add(ref _totalProcessed, value);
            Push(step, total);
        }

        private void Push(long stepProcessed, long totalProcessed)
        {
            if (_currentStep < 0 || _currentStep >= steps.Count)
            {
                return;
            }

            var (name, count) = steps[_currentStep];
            var stepPercent = count > 0 ? (int)(stepProcessed * 100L / count) : 100;
            var totalPercent = _totalFiles > 0 ? (int)(totalProcessed * 100L / _totalFiles) : 100;
            statusService.SetProgress(profileId, new ProfileProgress(totalPercent, name, stepPercent, steps.Count));
        }
    }
}
