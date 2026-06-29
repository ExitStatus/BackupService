namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Reads identity (volume serial, USB hardware serial, label) from removable drives. Windows-only at runtime
    /// (<see cref="WindowsUsbDeviceInspector"/>); a no-op off Windows (<see cref="NullUsbDeviceInspector"/>).
    /// </summary>
    public interface IUsbDeviceInspector
    {
        /// <summary>Every removable, ready drive currently connected, with its identity.</summary>
        IReadOnlyList<UsbDevice> EnumerateConnectedDevices();

        /// <summary>Reads the identity of one drive (e.g. <c>"D:"</c> or <c>"D:\"</c>), or null if it can't be read.</summary>
        UsbDevice? Inspect(string driveLetter);
    }
}
