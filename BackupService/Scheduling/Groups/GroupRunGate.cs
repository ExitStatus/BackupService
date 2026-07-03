using System.Collections.Concurrent;

namespace BackupService.Scheduling.Groups
{
    /// <summary>
    /// Default <see cref="IGroupRunGate"/>: one <see cref="SemaphoreSlim"/> (capacity 1) per Sequential
    /// group id. A run acquires its group's gate before starting and releases it when finished, so members
    /// of a Sequential group run one at a time. A null id (Parallel group / ungrouped) acquires nothing.
    /// Each run holds at most one group key, so there's no multi-key ordering to worry about.
    /// </summary>
    public sealed class GroupRunGate : IGroupRunGate
    {
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

        public async Task<IAsyncDisposable> AcquireAsync(int? sequentialGroupId, CancellationToken cancellationToken)
        {
            if (sequentialGroupId is not { } groupId)
            {
                return NoopReleaser.Instance;
            }

            var gate = _gates.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Releaser(gate);
        }

        private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
        {
            private bool _released;

            public ValueTask DisposeAsync()
            {
                if (!_released)
                {
                    _released = true;
                    gate.Release();
                }
                return ValueTask.CompletedTask;
            }
        }

        private sealed class NoopReleaser : IAsyncDisposable
        {
            public static readonly NoopReleaser Instance = new();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
