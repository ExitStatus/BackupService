using System.Globalization;

namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// Parses the PTP/MTP date string a camera reports for an object's modification/creation time
    /// ("YYYYMMDDThhmmss[.s]", optionally with a trailing Z/offset), tolerating ISO-ish variants. Pure and
    /// device-independent (unit-tested); the result is UTC-kind wall-clock to match how the other MTP date
    /// sources are normalised, so a round-trip with the stamped copy compares equal.
    /// </summary>
    internal static class MtpDateString
    {
        private static readonly string[] Formats =
        [
            "yyyyMMdd'T'HHmmss.f", "yyyyMMdd'T'HHmmss.ff", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm",
            "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
        ];

        public static DateTime? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim().TrimEnd('Z');

            if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return DateTime.SpecifyKind(exact, DateTimeKind.Utc);
            }
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
            {
                return DateTime.SpecifyKind(loose, DateTimeKind.Utc);
            }

            return null;
        }
    }
}
