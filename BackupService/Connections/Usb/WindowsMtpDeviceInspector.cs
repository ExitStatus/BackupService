using System.Runtime.Versioning;
using BackupService.Connections.Smb;
using MediaDevices;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Windows <see cref="IMtpDeviceInspector"/> over the MediaDevices (WPD) library. Identity is the WPD
    /// <c>DeviceId</c> (stable per physical device — it embeds the device serial — and readable without connecting),
    /// stored as the connection's MTP serial.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsMtpDeviceInspector(ILogger<WindowsMtpDeviceInspector> logger) : IMtpDeviceInspector
    {
        public IReadOnlyList<MtpDevice> EnumerateMtpDevices()
        {
            var devices = new List<MtpDevice>();
            try
            {
                foreach (var device in MediaDevice.GetDevices())
                {
                    try
                    {
                        devices.Add(new MtpDevice(device.DeviceId, DisplayName(device)));
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not enumerate MTP devices.");
            }

            return devices;
        }

        public bool IsConnected(string serial) =>
            EnumerateMtpDevices().Any(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<string> ListDirectories(string serial, string path)
        {
            try
            {
                using var device = FindDevice(serial);
                if (device is null)
                {
                    return [];
                }

                device.Connect();
                try
                {
                    var start = string.IsNullOrEmpty(path) ? @"\" : path;
                    return device.GetDirectories(start).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                }
                finally
                {
                    device.Disconnect();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not list MTP directories under '{Path}'.", path);
                return [];
            }
        }

        public StorageSpace? GetFreeSpace(string serial)
        {
            try
            {
                using var device = FindDevice(serial);
                if (device is null)
                {
                    return null;
                }

                device.Connect();
                try
                {
                    long total = 0;
                    long free = 0;
                    foreach (var drive in device.GetDrives())
                    {
                        total += drive.TotalSize;
                        free += drive.AvailableFreeSpace;
                    }

                    return total > 0 ? new StorageSpace(total, free, Unlimited: false) : null;
                }
                finally
                {
                    device.Disconnect();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read MTP free space.");
                return null;
            }
        }

        // Caller owns/disposes the returned device.
        private static MediaDevice? FindDevice(string serial)
        {
            MediaDevice? match = null;
            foreach (var device in MediaDevice.GetDevices())
            {
                if (match is null && string.Equals(device.DeviceId, serial, StringComparison.OrdinalIgnoreCase))
                {
                    match = device;
                }
                else
                {
                    device.Dispose();
                }
            }

            return match;
        }

        private static string DisplayName(MediaDevice device)
        {
            if (!string.IsNullOrWhiteSpace(device.FriendlyName))
            {
                return device.FriendlyName;
            }
            if (!string.IsNullOrWhiteSpace(device.Description))
            {
                return device.Description;
            }
            return string.IsNullOrWhiteSpace(device.Manufacturer) ? "Portable device" : device.Manufacturer;
        }
    }
}
