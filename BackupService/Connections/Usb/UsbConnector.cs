using BackupService.Connections.Smb;
using BackupService.Enumerations;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Default <see cref="IUsbConnector"/> — finds the connected device matching a USB connection and reports its
    /// mount path / free space. Branches by <see cref="UsbDeviceKind"/>: mass-storage over
    /// <see cref="IUsbDeviceInspector"/> + <see cref="DriveInfo"/>; MTP over <see cref="IMtpDeviceInspector"/>.
    /// Cross-platform (the Windows-only bits live behind the inspectors, which no-op off Windows).
    /// </summary>
    public sealed class UsbConnector(IUsbDeviceInspector inspector, IMtpDeviceInspector mtpInspector) : IUsbConnector
    {
        public IReadOnlyList<UsbDevice> EnumerateConnectedDevices() => inspector.EnumerateConnectedDevices();

        public string? FindMountPath(UsbConnectionInfo info)
        {
            if (info.Kind != UsbDeviceKind.MassStorage)
            {
                return null; // MTP has no drive/mount path.
            }

            foreach (var device in inspector.EnumerateConnectedDevices())
            {
                if (UsbDeviceMatcher.Matches(info, device))
                {
                    return device.MountPath;
                }
            }

            return null;
        }

        public Task<ConnectionTestResult> TestAsync(UsbConnectionInfo info, CancellationToken cancellationToken = default)
        {
            if (info.Kind == UsbDeviceKind.Mtp)
            {
                var connected = !string.IsNullOrEmpty(info.MtpSerial) && mtpInspector.IsConnected(info.MtpSerial);
                return Task.FromResult(connected
                    ? ConnectionTestResult.Success("The device is connected.")
                    : ConnectionTestResult.Failure("The device is not currently connected."));
            }

            var match = inspector.EnumerateConnectedDevices().FirstOrDefault(d => UsbDeviceMatcher.Matches(info, d));
            var result = match is null
                ? ConnectionTestResult.Failure("The device is not currently connected.")
                : ConnectionTestResult.Success($"Connected at {match.MountPath} ({(string.IsNullOrEmpty(match.Label) ? "no label" : match.Label)}).");
            return Task.FromResult(result);
        }

        public Task<StorageSpace?> GetFreeSpaceAsync(UsbConnectionInfo info, CancellationToken cancellationToken = default)
        {
            if (info.Kind == UsbDeviceKind.Mtp)
            {
                var space = string.IsNullOrEmpty(info.MtpSerial) ? null : mtpInspector.GetFreeSpace(info.MtpSerial);
                return Task.FromResult(space);
            }

            var mountPath = FindMountPath(info);
            if (mountPath is null)
            {
                return Task.FromResult<StorageSpace?>(null);
            }

            try
            {
                var drive = new DriveInfo(mountPath);
                return Task.FromResult<StorageSpace?>(new StorageSpace(drive.TotalSize, drive.AvailableFreeSpace, Unlimited: false));
            }
            catch
            {
                return Task.FromResult<StorageSpace?>(null);
            }
        }
    }
}
