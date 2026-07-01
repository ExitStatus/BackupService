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
                if (!drive.IsReady)
                {
                    continue;
                }

                // Removable media (USB sticks, card readers) is always a candidate. External NVMe/SSD enclosures over
                // a USB Attached SCSI bridge report DriveType.Fixed, so those are included only when the storage bus
                // is USB — which excludes the internal system disk (NVMe/SATA bus) while catching external drives.
                if (drive.DriveType != DriveType.Removable &&
                    !(drive.DriveType == DriveType.Fixed && IsUsbBusDrive(drive.Name)))
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

        private bool IsUsbBusDrive(string driveLetter)
        {
            var letter = NormaliseLetter(driveLetter);
            return letter is not null && TryReadStorageDescriptor(letter, out _, out var busType) && busType == BusTypeUsb;
        }

        public UsbDevice? Inspect(string driveLetter)
        {
            var letter = NormaliseLetter(driveLetter);
            if (letter is null)
            {
                return null;
            }

            var rootPath = letter + "\\";

            if (!TryReadVolume(rootPath, out var volumeSerial, out var label))
            {
                return null;
            }

            TryReadStorageDescriptor(letter, out var hardwareSerial, out _);
            return new UsbDevice(letter, rootPath, label, volumeSerial, hardwareSerial);
        }

        // Normalise "D:\", "D:" or "D" to the drive letter ("D:"), or null if it isn't a usable letter.
        private static string? NormaliseLetter(string driveLetter)
        {
            var letter = driveLetter.TrimEnd('\\', '/');
            if (letter.Length >= 1 && !letter.EndsWith(':'))
            {
                letter = letter[..1] + ":";
            }
            return letter.Length < 2 ? null : letter;
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

        // Reads the STORAGE_DEVICE_DESCRIPTOR for a volume: the (optional) hardware serial and the storage BusType.
        private bool TryReadStorageDescriptor(string letter, out string? serial, out int busType)
        {
            serial = null;
            busType = BusTypeUnknown;

            // Open the volume device (no access rights needed for a metadata query IOCTL).
            using var handle = CreateFile($@"\\.\{letter}", 0,
                FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            try
            {
                // STORAGE_PROPERTY_QUERY { PropertyId=StorageDeviceProperty(0); QueryType=PropertyStandardQuery(0); }
                var query = new byte[12];
                var output = new byte[1024];

                if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                        query, query.Length, output, output.Length, out var returned, IntPtr.Zero)
                    || returned < 32)
                {
                    return false;
                }

                // STORAGE_DEVICE_DESCRIPTOR.BusType is the uint at byte 28.
                busType = (int)BitConverter.ToUInt32(output, 28);

                // STORAGE_DEVICE_DESCRIPTOR.SerialNumberOffset is the uint at byte 24.
                var serialOffset = BitConverter.ToUInt32(output, 24);
                if (serialOffset != 0 && serialOffset < output.Length)
                {
                    var end = (int)serialOffset;
                    while (end < output.Length && output[end] != 0)
                    {
                        end++;
                    }

                    var read = Encoding.ASCII.GetString(output, (int)serialOffset, end - (int)serialOffset).Trim();
                    serial = string.IsNullOrEmpty(read) ? null : read;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the storage descriptor for {Drive}.", letter);
                return false;
            }
        }

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        private const int BusTypeUnknown = 0x00;
        private const int BusTypeUsb = 0x07;

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
