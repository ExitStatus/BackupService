using BackupService.Connections.Smb;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Locates and reports on the physical device a USB connection is bound to (over <see cref="IUsbDeviceInspector"/>
    /// + <see cref="DriveInfo"/>). A USB connection is only valid while its device is connected, so most of these
    /// return "not connected" / null when it isn't.
    /// </summary>
    public interface IUsbConnector
    {
        /// <summary>Every removable drive currently connected (for the editor's device picker).</summary>
        IReadOnlyList<UsbDevice> EnumerateConnectedDevices();

        /// <summary>The drive-root path (e.g. <c>"D:\"</c>) of the connected device matching <paramref name="info"/>, or null.</summary>
        string? FindMountPath(UsbConnectionInfo info);

        /// <summary>Reports whether the bound device is currently connected.</summary>
        Task<ConnectionTestResult> TestAsync(UsbConnectionInfo info, CancellationToken cancellationToken = default);

        /// <summary>The connected device's total/free capacity, or null when it isn't connected.</summary>
        Task<StorageSpace?> GetFreeSpaceAsync(UsbConnectionInfo info, CancellationToken cancellationToken = default);
    }
}
