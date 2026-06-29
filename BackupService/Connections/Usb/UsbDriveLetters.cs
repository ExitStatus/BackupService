namespace BackupService.Connections.Usb
{
    /// <summary>
    /// Decodes the <c>dbcv_unitmask</c> bit field carried by a <c>WM_DEVICECHANGE</c> volume event into drive
    /// letters (bit 0 = <c>A:</c>, bit 1 = <c>B:</c>, …). Pure, so it's unit-tested.
    /// </summary>
    public static class UsbDriveLetters
    {
        public static IReadOnlyList<string> FromUnitMask(uint unitMask)
        {
            var letters = new List<string>();
            for (var i = 0; i < 26; i++)
            {
                if ((unitMask & (1u << i)) != 0)
                {
                    letters.Add($"{(char)('A' + i)}:");
                }
            }

            return letters;
        }
    }
}
