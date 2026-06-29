using BackupService.Connections.Smb;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Enumerates and browses MTP/PTP portable devices via the Windows Portable Devices API (MediaDevices). Windows-only
    /// at runtime (<see cref="WindowsMtpDeviceInspector"/>); a no-op off Windows (<see cref="NullMtpDeviceInspector"/>).
    /// </summary>
    public interface IMtpDeviceInspector
    {
        /// <summary>Every connected MTP/PTP device, with its serial + friendly name.</summary>
        IReadOnlyList<MtpDevice> EnumerateMtpDevices();

        /// <summary>Whether a device with <paramref name="serial"/> is currently connected.</summary>
        bool IsConnected(string serial);

        /// <summary>
        /// Lists the immediate sub-folder paths under <paramref name="path"/> on the device with <paramref name="serial"/>
        /// (device-absolute backslash paths; empty <paramref name="path"/> = the device root). For the folder browser.
        /// </summary>
        IReadOnlyList<string> ListDirectories(string serial, string path);

        /// <summary>The device's total/free storage, or null if it can't be determined or isn't connected.</summary>
        StorageSpace? GetFreeSpace(string serial);
    }
}
