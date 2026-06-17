namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="ILogWatcher"/>. Coalesces a burst of <see cref="Notify"/> calls into at most
    /// one <see cref="Changed"/> per <see cref="DebounceMilliseconds"/> window via a single timer.
    /// Unlike a reset-on-every-event debounce, the window is <b>not</b> re-armed by subsequent writes
    /// while one is already pending, so continuous logging (e.g. a large backup) still raises
    /// <see cref="Changed"/> roughly once per window rather than going silent until the writes stop.
    /// </summary>
    public sealed class LogWatcher : ILogWatcher, IDisposable
    {
        // Coalescing window. Tunable: smaller = more responsive, larger = fewer refreshes under load.
        private const int DebounceMilliseconds = 500;

        private readonly object _gate = new();
        private readonly Timer _timer;
        private bool _scheduled;
        private bool _disposed;

        public event Action? Changed;

        public LogWatcher()
        {
            _timer = new Timer(_ => Fire(), state: null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Notify()
        {
            lock (_gate)
            {
                // Already disposed, or a fire is already pending for this window — nothing to do.
                if (_disposed || _scheduled)
                {
                    return;
                }

                _scheduled = true;
                _timer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
        }

        private void Fire()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Clear before invoking so writes arriving during the callback open the next window.
                _scheduled = false;
            }

            var handlers = Changed;
            if (handlers is null)
            {
                return;
            }

            // Invoke subscribers in isolation so one bad handler can't break the others or the timer.
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch
                {
                    // A subscriber's failure must not stop notifications to the rest.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
            _timer.Dispose();
        }
    }
}
