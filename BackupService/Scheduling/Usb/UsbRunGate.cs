using System.Collections.Concurrent;

namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Default <see cref="IUsbRunGate"/>: one <see cref="SemaphoreSlim"/> (capacity 1) per USB connection id. A run
    /// acquires the gate for each USB device it uses before starting and releases them when it finishes, so runs
    /// sharing a device are serialised. Gates are acquired in ascending id order so a run using two USB devices can't
    /// deadlock against another using the same pair.
    /// </summary>
    public sealed class UsbRunGate : IUsbRunGate
    {
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();

        public async Task<IAsyncDisposable> AcquireAsync(IReadOnlyCollection<int> usbConnectionIds, CancellationToken cancellationToken)
        {
            var ordered = usbConnectionIds.Distinct().OrderBy(id => id).ToList();
            var acquired = new List<SemaphoreSlim>(ordered.Count);
            try
            {
                foreach (var id in ordered)
                {
                    var gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(cancellationToken);
                    acquired.Add(gate);
                }
            }
            catch
            {
                // Release anything taken before the failure (e.g. cancellation waiting for a later gate).
                foreach (var gate in acquired)
                {
                    gate.Release();
                }
                throw;
            }

            return new Releaser(acquired);
        }

        private sealed class Releaser(List<SemaphoreSlim> gates) : IAsyncDisposable
        {
            private bool _released;

            public ValueTask DisposeAsync()
            {
                if (!_released)
                {
                    _released = true;
                    foreach (var gate in gates)
                    {
                        gate.Release();
                    }
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}
