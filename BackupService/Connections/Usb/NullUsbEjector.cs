namespace BackupService.Connections.Usb
{
    /// <summary>No-op <see cref="IUsbEjector"/> for non-Windows hosts (USB eject is Windows-only).</summary>
    public sealed class NullUsbEjector : IUsbEjector
    {
        public bool TryEject(string mountPath) => false;
    }
}
