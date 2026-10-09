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
        // Lock holders per profile: two tabs can have the same profile open, and one closing mustn't unlock the other.
        private readonly Dictionary<int, int> _lockCounts = new();
        private readonly ConcurrentDictionary<int, int> _runLogs = new();
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

        // The single-run guard. Deliberately separate from the displayed status: a handler shows Error as soon as a
        // run fails, while the runner is still finishing it (summary, run record, last-run stamp) — that must not let
        // a second run start on the same folders, nor that run's end clear the second one's state.
        private readonly ConcurrentDictionary<int, byte> _activeRuns = new();

        public bool IsRunning(int profileId) => _activeRuns.ContainsKey(profileId);

        public bool TryBeginRun(int profileId)
        {
            lock (_runLock)
            {
                // Check-and-set must be atomic so two callers can't both begin the same profile.
                if (!_activeRuns.TryAdd(profileId, 0))
                {
                    return false;
                }
                _statuses[profileId] = ProfileStatus.Running;
                _runLogs.TryRemove(profileId, out _); // none yet — a queued run creates its log once it starts
            }

            Changed?.Invoke(profileId);
            return true;
        }

        public void EndRun(int profileId, ProfileStatus finalStatus)
        {
            lock (_runLock)
            {
                _activeRuns.TryRemove(profileId, out _);
                _runLogs.TryRemove(profileId, out _);
            }
            Set(profileId, finalStatus);
        }

        public void SetRunLog(int profileId, int operationLogId)
        {
            _runLogs[profileId] = operationLogId;
            ProgressChanged?.Invoke(profileId);
        }

        public int? GetRunLog(int profileId) =>
            IsRunning(profileId) && _runLogs.TryGetValue(profileId, out var logId) ? logId : null;

        public void Remove(int profileId)
        {
            _statuses.TryRemove(profileId, out _);
            _activeRuns.TryRemove(profileId, out _);
            _runLogs.TryRemove(profileId, out _);
            lock (_lockCounts)
            {
                _lockCounts.Remove(profileId);
            }
        }

        public void Lock(int profileId)
        {
            lock (_lockCounts)
            {
                _lockCounts[profileId] = _lockCounts.GetValueOrDefault(profileId) + 1;
            }
        }

        public void Unlock(int profileId)
        {
            lock (_lockCounts)
            {
                if (_lockCounts.TryGetValue(profileId, out var count))
                {
                    if (count <= 1)
                    {
                        _lockCounts.Remove(profileId);
                    }
                    else
                    {
                        _lockCounts[profileId] = count - 1;
                    }
                }
            }
        }

        public bool IsLocked(int profileId)
        {
            lock (_lockCounts)
            {
                return _lockCounts.ContainsKey(profileId);
            }
        }
    }
}
