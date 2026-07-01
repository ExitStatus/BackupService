using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// A minimal connection projection (id + name + type) for pickers (e.g. the folder-pair
    /// source/target location dropdowns). <see cref="UsbKind"/> is set only for USB connections
    /// (null otherwise) so a picker can offer read/write mass-storage but exclude read-only MTP.
    /// </summary>
    public sealed record ConnectionSummary(int Id, string Name, ConnectionType Type, UsbDeviceKind? UsbKind = null);
}
