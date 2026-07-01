namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Safely removes (ejects) the USB mass-storage device behind a drive — the "Safely Remove Hardware" operation —
    /// so the user can unplug it after an automatic run. Best-effort: returns whether the eject succeeded.
    /// </summary>
    public interface IUsbEjector
    {
        /// <summary>Ejects the removable device backing <paramref name="mountPath"/> (e.g. <c>"D:\"</c>).</summary>
        bool TryEject(string mountPath);
    }
}
