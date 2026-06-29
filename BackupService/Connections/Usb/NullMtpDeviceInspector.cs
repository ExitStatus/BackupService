using BackupService.Connections.Smb;

namespace BackupService.Connections.Usb
{
    /// <summary>No-op <see cref="IMtpDeviceInspector"/> used off Windows (WPD/MTP is Windows-only).</summary>
    public sealed class NullMtpDeviceInspector : IMtpDeviceInspector
    {
        public IReadOnlyList<MtpDevice> EnumerateMtpDevices() => [];

        public bool IsConnected(string serial) => false;

        public IReadOnlyList<string> ListDirectories(string serial, string path) => [];

        public StorageSpace? GetFreeSpace(string serial) => null;
    }
}
