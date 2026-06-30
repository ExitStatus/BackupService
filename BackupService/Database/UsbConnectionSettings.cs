using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// USB-device settings — a 1:1 child of a <see cref="Connection"/> (like <see cref="SmbConnectionSettings"/>).
    /// A USB connection is bound to a <b>specific physical device</b>, not a drive letter (which is reused), so it
    /// records identity captured when the device was registered: the USB <see cref="HardwareSerial"/> (preferred,
    /// but not every device reports one) and the <see cref="VolumeSerial"/> (always read, the fallback). On connect
    /// the arriving drive's identity is matched against these. There is no secret to encrypt.
    /// </summary>
    public class UsbConnectionSettings
    {
        /// <summary>Primary key and FK to <see cref="Connection"/>.</summary>
        public int ConnectionId { get; set; }

        public Connection? Connection { get; set; }

        /// <summary>Whether this is a mass-storage drive or an MTP portable device — selects identity/detection/access.</summary>
        public UsbDeviceKind Kind { get; set; }

        /// <summary>The device's USB hardware serial, when it reports one (mass-storage preferred match key). Null otherwise.</summary>
        [MaxLength(256)]
        public string? HardwareSerial { get; set; }

        /// <summary>The volume serial number of the registered drive (mass-storage fallback match key); empty for MTP.</summary>
        [MaxLength(64)]
        public required string VolumeSerial { get; set; }

        /// <summary>The MTP device serial — the match key for an <see cref="UsbDeviceKind.Mtp"/> device. Null for mass-storage.</summary>
        [MaxLength(256)]
        public string? MtpSerial { get; set; }

        /// <summary>The volume label captured at registration, for display.</summary>
        [MaxLength(256)]
        public string? DeviceLabel { get; set; }

        /// <summary>Folder on the device the connection is rooted at (relative to the drive root); null = drive root.</summary>
        [MaxLength(1024)]
        public string? RootFolder { get; set; }

        /// <summary>Master switch for this device's connect/disconnect desktop notifications (store default true).</summary>
        public bool NotificationsEnabled { get; set; } = true;

        /// <summary>Show a desktop notification when the device is plugged in (store default true).</summary>
        public bool NotifyOnConnect { get; set; } = true;

        /// <summary>Show a desktop notification when the device is unplugged (store default true).</summary>
        public bool NotifyOnDisconnect { get; set; } = true;
    }
}
