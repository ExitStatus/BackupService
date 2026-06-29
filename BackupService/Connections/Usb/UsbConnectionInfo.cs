using BackupService.Enumerations;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Runtime view of a USB connection: the device kind + identity used to locate the currently-connected device
    /// (mass-storage: hardware serial preferred, volume serial fallback; MTP: <see cref="MtpSerial"/>) and the folder
    /// it's rooted at. Built by <see cref="IConnectionResolver"/>. No secret.
    /// </summary>
    public sealed record UsbConnectionInfo(
        UsbDeviceKind Kind,
        string? HardwareSerial,
        string VolumeSerial,
        string? MtpSerial,
        string? RootFolder);
}
