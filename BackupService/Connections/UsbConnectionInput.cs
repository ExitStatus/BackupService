using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// Service-layer input for creating/updating a USB connection. Carries the device identity captured when the
    /// device was registered and the folder on the device the connection is rooted at. No secret.
    /// <para>For <see cref="UsbDeviceKind.MassStorage"/> the identity is the hardware serial (when available) plus the
    /// always-present volume serial. For <see cref="UsbDeviceKind.Mtp"/> it is <see cref="MtpSerial"/>.</para>
    /// </summary>
    public sealed record UsbConnectionInput(
        UsbDeviceKind Kind,
        string? HardwareSerial,
        string VolumeSerial,
        string? MtpSerial,
        string? DeviceLabel,
        string? RootFolder,
        bool NotificationsEnabled = true,
        bool NotifyOnConnect = true,
        bool NotifyOnDisconnect = true);
}
