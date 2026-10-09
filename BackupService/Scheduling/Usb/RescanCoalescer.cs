namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Runs a scan in the background when asked, folding a burst of requests into one run — but never dropping one:
    /// a request that arrives while a scan is already running gets one more scan after it, so a change that happens
    /// part-way through (a device that registers just after a scan looked) is still picked up. Only one scan runs at
    /// a time. The scan handles its own errors; any that escape are swallowed so the next request still runs.
    /// </summary>
    internal sealed class RescanCoalescer(Func<Task> scan)
    {
        private int _requested;
        private int _running;

        public void Request()
        {
            Interlocked.Exchange(ref _requested, 1);
            TryStart();
        }

        private void TryStart()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = Task.Run(RunAsync);
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (Interlocked.Exchange(ref _requested, 0) == 1)
                {
                    try
                    {
                        await scan();
                    }
                    catch (Exception)
                    {
                        // The scan logs its own failures (and is cancelled on shutdown).
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }

            // A request made after the loop's last check but before _running was cleared found a scan "running" and
            // left it to that one — start it here.
            if (Volatile.Read(ref _requested) == 1)
            {
                TryStart();
            }
        }
    }
}
