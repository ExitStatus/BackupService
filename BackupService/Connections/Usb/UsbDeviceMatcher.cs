namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Decides whether a connected device is the one a USB connection was bound to. Pure (so it's unit-tested):
    /// <b>prefer the hardware serial</b> — when both the connector and the device report one, they must be equal;
    /// otherwise fall back to comparing the volume serial. This lets the same physical device be recognised across
    /// drive-letter changes (and reformats, when a hardware serial is available) while still telling two devices
    /// apart when neither reports a hardware serial.
    /// </summary>
    public static class UsbDeviceMatcher
    {
        public static bool Matches(string? connectorHardwareSerial, string connectorVolumeSerial, string? deviceHardwareSerial, string deviceVolumeSerial)
        {
            if (!string.IsNullOrEmpty(connectorHardwareSerial) && !string.IsNullOrEmpty(deviceHardwareSerial))
            {
                return string.Equals(connectorHardwareSerial, deviceHardwareSerial, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrEmpty(connectorVolumeSerial)
                && string.Equals(connectorVolumeSerial, deviceVolumeSerial, StringComparison.OrdinalIgnoreCase);
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
