namespace BackupService.Connections.Usb
{
    /// <summary>No-op <see cref="IUsbDeviceInspector"/> used off Windows (USB device identity is Windows-only).</summary>
    public sealed class NullUsbDeviceInspector : IUsbDeviceInspector
    {
        public IReadOnlyList<UsbDevice> EnumerateConnectedDevices() => [];

        public UsbDevice? Inspect(string driveLetter) => null;
    }
}
