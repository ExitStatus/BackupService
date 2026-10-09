namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Decides whether a connected device is the one a USB connection was bound to. Pure (so it's unit-tested).
    /// The <b>volume serial must always match</b>, and when both the connector and the device also report a hardware
    /// serial, that must match too. Matching on the hardware serial alone isn't safe: every partition of one disk
    /// shares the disk's serial, and every card in a USB card reader shares the reader's — so a connection bound to
    /// one card would also run (and, with deletions on, mirror) against any other card put in the same reader. The
    /// volume serial is what tells those apart. The cost: reformatting a drive gives it a new volume serial, so its
    /// connection has to be re-pointed at the device (pick it again in the connection editor).
    /// </summary>
    public static class UsbDeviceMatcher
    {
        public static bool Matches(string? connectorHardwareSerial, string connectorVolumeSerial, string? deviceHardwareSerial, string deviceVolumeSerial)
        {
            if (string.IsNullOrEmpty(connectorVolumeSerial)
                || !string.Equals(connectorVolumeSerial, deviceVolumeSerial, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Same volume — when both report a hardware serial it must agree too (two volumes can share a serial).
            return string.IsNullOrEmpty(connectorHardwareSerial)
                || string.IsNullOrEmpty(deviceHardwareSerial)
                || string.Equals(connectorHardwareSerial, deviceHardwareSerial, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Matches a mass-storage connection's stored identity against a connected drive.</summary>
        public static bool Matches(UsbConnectionInfo connection, UsbDevice device) =>
            Matches(connection.HardwareSerial, connection.VolumeSerial, device.HardwareSerial, device.VolumeSerial);

        /// <summary>Matches an MTP connection against a connected portable device by serial (case-insensitive).</summary>
        public static bool MatchesMtp(string? connectorSerial, string deviceSerial) =>
            !string.IsNullOrEmpty(connectorSerial)
            && string.Equals(connectorSerial, deviceSerial, StringComparison.OrdinalIgnoreCase);
    }
}
