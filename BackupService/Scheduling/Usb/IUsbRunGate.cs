namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Serialises backup runs that touch the same USB device. USB I/O is slow, so two profiles that reference the
    /// same USB connection (an MTP camera or a mass-storage drive) must not run against it at once — they queue and
    /// run one at a time. Keyed by connection id (each USB connection is bound to one physical device).
    /// </summary>
    public interface IUsbRunGate
    {
        /// <summary>
        /// Waits until every given USB connection is free, then takes exclusive use of them; the returned handle
        /// releases them on dispose. An empty set is a no-op. Cancellation (Stop/shutdown) aborts the wait.
        /// </summary>
        Task<IAsyncDisposable> AcquireAsync(IReadOnlyCollection<int> usbConnectionIds, CancellationToken cancellationToken);
    }
}
