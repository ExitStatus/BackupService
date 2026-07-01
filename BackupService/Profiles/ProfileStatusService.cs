using System.Collections.Concurrent;
using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IProfileStatusService"/>. Thread-safe (statuses are mutated from the
    /// background scheduler/runner and read by the UI), backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    public sealed class ProfileStatusService : IProfileStatusService
    {
        private readonly ConcurrentDictionary<int, ProfileStatus> _statuses = new();
        private readonly ConcurrentDictionary<int, ProfileProgress> _progress = new();
        private readonly ConcurrentDictionary<int, byte> _locked = new();
        private readonly object _runLock = new();

        public event Action<int>? Changed;

        public event Action<int>? ProgressChanged;

        public ProfileStatus Get(int profileId) =>
            _statuses.TryGetValue(profileId, out var status) ? status : ProfileStatus.Idle;

        public void Set(int profileId, ProfileStatus status)
        {
            _statuses[profileId] = status;
            // A finished/failed run is no longer making progress — drop any stale percent.
            if (status != ProfileStatus.Running)
            {
                _progress.TryRemove(profileId, out _);
            }
            Changed?.Invoke(profileId);
        }

        public int? GetProgress(int profileId) =>
            _progress.TryGetValue(profileId, out var p) ? p.TotalPercent : null;

        public ProfileProgress? GetProgressDetail(int profileId) =>
            _progress.TryGetValue(profileId, out var p) ? p : null;

        public void SetProgress(int profileId, int percent)
        {
            var clamped = Math.Clamp(percent, 0, 100);
            // A bare percent (no step detail) is a single-step snapshot: step percent tracks the total.
            SetProgress(profileId, new ProfileProgress(clamped, null, clamped, 1));
        }

        public void SetProgress(int profileId, ProfileProgress progress)
        {
            progress = progress with
            {
                TotalPercent = Math.Clamp(progress.TotalPercent, 0, 100),
                StepPercent = Math.Clamp(progress.StepPercent, 0, 100),
            };
            // Only notify when the snapshot actually changes (caps UI churn — the fields are all integers /
            // the step name, so a run pushes a bounded number of updates).
            if (_progress.TryGetValue(profileId, out var existing) && existing == progress)
            {
                return;
            }
            _progress[profileId] = progress;
            ProgressChanged?.Invoke(profileId);
        }

        public bool IsRunning(int profileId) => Get(profileId) == ProfileStatus.Running;

        public bool TryBeginRun(int profileId)
        {
            bool began;
            lock (_runLock)
            {
                // Check-and-set must be atomic so two callers can't both begin the same profile.
                began = Get(profileId) != ProfileStatus.Running;
                if (began)
                {
                    _statuses[profileId] = ProfileStatus.Running;
                }
            }

            if (began)
            {
                Changed?.Invoke(profileId);
            }

            return began;
        }

        public void Remove(int profileId)
        {
            _statuses.TryRemove(profileId, out _);
            _locked.TryRemove(profileId, out _);
        }

        public void Lock(int profileId) => _locked[profileId] = 0;

        public void Unlock(int profileId) => _locked.TryRemove(profileId, out _);

        public bool IsLocked(int profileId) => _locked.ContainsKey(profileId);
    }
}
