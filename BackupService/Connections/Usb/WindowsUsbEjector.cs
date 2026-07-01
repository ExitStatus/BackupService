using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Windows <see cref="IUsbEjector"/> — the "Safely Remove Hardware" operation. From a drive letter it reads the
    /// storage device number (<c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c>), finds the matching disk device instance via
    /// SetupAPI, walks up to its removable parent, and calls <c>CM_Request_Device_Eject</c>. Pure P/Invoke (SetupAPI +
    /// CfgMgr32), no NuGet package; best-effort (logs and returns false on failure).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsUsbEjector(ILogger<WindowsUsbEjector> logger) : IUsbEjector
    {
        public bool TryEject(string mountPath)
        {
            try
            {
                var letter = NormalizeLetter(mountPath);
                if (letter is null)
                {
                    return false;
                }

                var deviceNumber = GetDeviceNumber(letter);
                if (deviceNumber < 0)
                {
                    return false;
                }

                var devInst = FindDiskDevInst(deviceNumber);
                if (devInst == 0)
                {
                    logger.LogWarning("USB eject: no disk device found for {Drive} (device #{Number}).", letter, deviceNumber);
                    return false;
                }

                // The disk's parent is the removable (USBSTOR/USB) device that "Safely Remove Hardware" ejects.
                var target = CM_Get_Parent(out var parent, devInst, 0) == CrSuccess ? parent : devInst;
                if (RequestEject(target))
                {
                    logger.LogInformation("USB eject: {Drive} ejected.", letter);
                    return true;
                }

                logger.LogWarning("USB eject: the device for {Drive} could not be ejected (it may be in use).", letter);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "USB eject: failed for {Mount}.", mountPath);
                return false;
            }
        }

        // "D:\" / "D:" / "D" -> "D:" (or null if not a drive letter).
        private static string? NormalizeLetter(string mountPath)
        {
            var trimmed = (mountPath ?? string.Empty).TrimEnd('\\', '/');
            if (trimmed.Length >= 1 && !trimmed.EndsWith(':'))
            {
                trimmed = trimmed[..1] + ":";
            }
            return trimmed.Length == 2 && char.IsLetter(trimmed[0]) ? trimmed : null;
        }

        private long GetDeviceNumber(string letter)
        {
            using var handle = CreateFile($@"\\.\{letter}", 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return -1;
            }

            var output = new byte[12]; // STORAGE_DEVICE_NUMBER { int DeviceType; uint DeviceNumber; int PartitionNumber; }
            if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, output, output.Length, out _, IntPtr.Zero))
            {
                return -1;
            }

            return BitConverter.ToUInt32(output, 4); // DeviceNumber
        }

        // Finds the device-instance handle of the disk whose storage device number matches.
        private uint FindDiskDevInst(long deviceNumber)
        {
            var diskGuid = GuidDevInterfaceDisk;
            var info = SetupDiGetClassDevs(ref diskGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (info == InvalidHandleValue)
            {
                return 0;
            }

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint index = 0; SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref diskGuid, index, ref interfaceData); index++)
                {
                    if (TryGetDevicePath(info, ref interfaceData, out var path, out var devInst)
                        && GetDeviceNumberFromPath(path) == deviceNumber)
                    {
                        return devInst;
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(info);
            }

            return 0;
        }

        private static bool TryGetDevicePath(IntPtr info, ref SP_DEVICE_INTERFACE_DATA interfaceData, out string path, out uint devInst)
        {
            path = string.Empty;
            devInst = 0;

            SetupDiGetDeviceInterfaceDetail(info, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required == 0)
            {
                return false;
            }

            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize: 8 on 64-bit, 6 on 32-bit (fixed part before the path chars).
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiGetDeviceInterfaceDetail(info, ref interfaceData, buffer, required, out _, ref devInfo))
                {
                    return false;
                }

                path = Marshal.PtrToStringUni(buffer + 4) ?? string.Empty; // DevicePath follows the 4-byte cbSize
                devInst = devInfo.DevInst;
                return !string.IsNullOrEmpty(path);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private long GetDeviceNumberFromPath(string devicePath)
        {
            using var handle = CreateFile(devicePath, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return -1;
            }

            var output = new byte[12];
            return DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, output, output.Length, out _, IntPtr.Zero)
                ? BitConverter.ToUInt32(output, 4)
                : -1;
        }

        // A device may briefly veto (in use); retry a few times.
        private bool RequestEject(uint devInst)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var vetoName = new StringBuilder(260);
                if (CM_Request_Device_Eject(devInst, out var vetoType, vetoName, 260, 0) == CrSuccess && vetoType == 0)
                {
                    return true;
                }
                Thread.Sleep(400);
            }
            return false;
        }

        // ---- interop ----

        private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
        private const int DigcfPresent = 0x2;
        private const int DigcfDeviceInterface = 0x10;
        private const uint CrSuccess = 0;
        private static readonly IntPtr InvalidHandleValue = new(-1);
        private static Guid GuidDevInterfaceDisk = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint CM_Request_Device_Eject(uint dnDevInst, out int pVetoType, StringBuilder pszVetoName,
            int ulNameLength, uint ulFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandleEx CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandleEx device, uint controlCode, IntPtr inBuffer,
            int inBufferSize, byte[] outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

        private sealed class SafeFileHandleEx() : SafeHandleZeroOrMinusOneIsInvalid(true)
        {
            protected override bool ReleaseHandle() => CloseHandle(handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
