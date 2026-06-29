namespace BackupService.Connections.Usb
{
    /// <summary>
    /// A currently-connected removable drive's identity, as read by <see cref="IUsbDeviceInspector"/>.
    /// <see cref="DriveLetter"/> is e.g. <c>"D:"</c>; <see cref="MountPath"/> is the drive root <c>"D:\"</c>.
    /// <see cref="HardwareSerial"/> is null when the device doesn't report one.
    /// </summary>
    public sealed record UsbDevice(
        string DriveLetter,
        string MountPath,
        string Label,
        string VolumeSerial,
        string? HardwareSerial);
}
