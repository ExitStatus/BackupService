using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// What kind of USB device a <see cref="BackupService.Database.UsbConnectionSettings"/> is bound to. Both are
    /// device-bound and auto-run a profile on connect; they differ only in identity, detection and file access.
    /// </summary>
    public enum UsbDeviceKind
    {
        /// <summary>A removable mass-storage drive with a drive letter (USB stick, card reader, camera in MSC mode).</summary>
        [Description("USB drive")]
        MassStorage = 0,

        /// <summary>An MTP/PTP portable device with no drive letter (most cameras and phones), accessed via WPD.</summary>
        [Description("Portable device (MTP)")]
        Mtp = 1,
    }
}
