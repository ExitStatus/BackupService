using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Windows <see cref="IUsbDeviceInspector"/>. The volume serial comes from <c>GetVolumeInformation</c>; the USB
    /// hardware serial (when the device reports one) from <c>DeviceIoControl(IOCTL_STORAGE_QUERY_PROPERTY)</c> against
    /// the volume. Pure P/Invoke — no NuGet package.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsUsbDeviceInspector(ILogger<WindowsUsbDeviceInspector> logger) : IUsbDeviceInspector
    {
        public IReadOnlyList<UsbDevice> EnumerateConnectedDevices()
        {
            var devices = new List<UsbDevice>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                {
                    continue;
                }

                var device = Inspect(drive.Name);
                if (device is not null)
                {
                    devices.Add(device);
                }
            }

            return devices;
        }

        public UsbDevice? Inspect(string driveLetter)
        {
            // Normalise "D:\", "D:" or "D" to the letter ("D:") and the root path ("D:\").
            var letter = driveLetter.TrimEnd('\\', '/');
            if (letter.Length >= 1 && !letter.EndsWith(':'))
            {
                letter = letter[..1] + ":";
            }
            if (letter.Length < 2)
            {
                return null;
            }

            var rootPath = letter + "\\";

            if (!TryReadVolume(rootPath, out var volumeSerial, out var label))
            {
                return null;
            }

            var hardwareSerial = TryReadHardwareSerial(letter);
            return new UsbDevice(letter, rootPath, label, volumeSerial, hardwareSerial);
        }

        private bool TryReadVolume(string rootPath, out string volumeSerial, out string label)
        {
            volumeSerial = string.Empty;
            label = string.Empty;

            var labelBuffer = new StringBuilder(261);
            var fsBuffer = new StringBuilder(261);
            if (!GetVolumeInformation(rootPath, labelBuffer, labelBuffer.Capacity, out var serialNumber,
                    out _, out _, fsBuffer, fsBuffer.Capacity))
            {
                return false;
            }

            volumeSerial = serialNumber.ToString("X8");
            label = labelBuffer.ToString();
            return true;
        }

        private string? TryReadHardwareSerial(string letter)
        {
            // Open the volume device (no access rights needed for a metadata query IOCTL).
            using var handle = CreateFile($@"\\.\{letter}", 0,
                FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            try
            {
                // STORAGE_PROPERTY_QUERY { PropertyId=StorageDeviceProperty(0); QueryType=PropertyStandardQuery(0); }
                var query = new byte[12];
                var output = new byte[1024];

                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                        query, query.Length, output, output.Length, out var returned, IntPtr.Zero)
                    || returned < 28)
                {
                    return null;
                }

                // STORAGE_DEVICE_DESCRIPTOR.SerialNumberOffset is the uint at byte 24.
                var serialOffset = BitConverter.ToUInt32(output, 24);
                if (serialOffset == 0 || serialOffset >= output.Length)
                {
                    return null;
                }

                var end = (int)serialOffset;
                while (end < output.Length && output[end] != 0)
                {
                    end++;
                }

                var serial = Encoding.ASCII.GetString(output, (int)serialOffset, end - (int)serialOffset).Trim();
                return string.IsNullOrEmpty(serial) ? null : serial;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the USB hardware serial for {Drive}.", letter);
                return null;
            }
        }

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandleEx CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandleEx device,
            uint controlCode,
            byte[] inBuffer,
            int inBufferSize,
            byte[] outBuffer,
            int outBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        // A minimal SafeHandle so the device handle is always closed.
        private sealed class SafeFileHandleEx() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
        {
            protected override bool ReleaseHandle() => CloseHandle(handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
