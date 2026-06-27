using System.Collections.Concurrent;
using BackupService.Enumerations;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Default <see cref="IScheduledTaskStatusService"/>. Thread-safe (mutated from the background
    /// scheduler/runner, read by the UI), backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// A trimmed copy of <c>ProfileStatusService</c> (no progress percent).
    /// </summary>
    public sealed class ScheduledTaskStatusService : IScheduledTaskStatusService
    {
        private readonly ConcurrentDictionary<int, ProfileStatus> _statuses = new();
        private readonly ConcurrentDictionary<int, byte> _locked = new();
        private readonly object _runLock = new();

        public event Action<int>? Changed;

        public ProfileStatus Get(int taskId) =>
            _statuses.TryGetValue(taskId, out var status) ? status : ProfileStatus.Idle;

        public void Set(int taskId, ProfileStatus status)
        {
            _statuses[taskId] = status;
            Changed?.Invoke(taskId);
        }

        public bool IsRunning(int taskId) => Get(taskId) == ProfileStatus.Running;

        public bool TryBeginRun(int taskId)
        {
            bool began;
            lock (_runLock)
            {
                // Check-and-set must be atomic so two callers can't both begin the same task.
                began = Get(taskId) != ProfileStatus.Running;
                if (began)
                {
                    _statuses[taskId] = ProfileStatus.Running;
                }
            }

            if (began)
            {
                Changed?.Invoke(taskId);
            }

            return began;
        }

        public void Remove(int taskId)
        {
            _statuses.TryRemove(taskId, out _);
            _locked.TryRemove(taskId, out _);
        }

        public void Lock(int taskId) => _locked[taskId] = 0;

        public void Unlock(int taskId) => _locked.TryRemove(taskId, out _);

        public bool IsLocked(int taskId) => _locked.ContainsKey(taskId);
    }
}
