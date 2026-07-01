namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Pure decision (so it's unit-tested) for whether a device-triggered profile may auto-start: every USB
    /// connection it references — its source and/or target — must currently be connected. Non-USB sides (local,
    /// SMB, Google Drive) need no device. When both source and target are USB devices, this is only true once
    /// both are plugged in.
    /// </summary>
    public static class UsbTriggerEvaluator
    {
        /// <param name="isUsb">Whether a connection id is a USB connection.</param>
        /// <param name="isConnected">Whether that USB connection's device is currently connected.</param>
        public static bool AllRequiredUsbConnected(
            int? sourceConnectionId, int? targetConnectionId, Func<int, bool> isUsb, Func<int, bool> isConnected)
        {
            foreach (var connectionId in new[] { sourceConnectionId, targetConnectionId })
            {
                if (connectionId is { } id && isUsb(id) && !isConnected(id))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
