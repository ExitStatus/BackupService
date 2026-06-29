namespace BackupService.Connections.Usb
{
    /// <summary>
    /// A currently-connected MTP/PTP portable device's identity, as read by <see cref="IMtpDeviceInspector"/>.
    /// <see cref="Serial"/> is the stable match key; <see cref="Name"/> is for display.
    /// </summary>
    public sealed record MtpDevice(string Serial, string Name);
}
