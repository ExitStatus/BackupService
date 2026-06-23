using System.Globalization;

namespace BackupService.Extensions
{
    /// <summary>
    /// Formats a byte count as a human-readable size (B/KB/MB/GB/TB/PB, base 1024). The C# counterpart of
    /// the dashboard's JS byte formatter: one decimal place when the scaled value is under 10, otherwise none.
    /// </summary>
    public static class ByteSize
    {
        private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

        /// <summary>Humanizes <paramref name="bytes"/>, or returns "—" when null.</summary>
        public static string Humanize(long? bytes) => bytes is { } value ? Humanize(value) : "—";

        public static string Humanize(long bytes)
        {
            double scaled = Math.Abs(bytes);
            var unit = 0;
            while (scaled >= 1024 && unit < Units.Length - 1)
            {
                scaled /= 1024;
                unit++;
            }

            var sign = bytes < 0 ? "-" : string.Empty;
            // Whole bytes show no decimals; larger units show one decimal under 10, none at or above 10.
            var format = unit == 0 ? "0" : scaled < 10 ? "0.0" : "0";
            return $"{sign}{scaled.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
        }
    }
}
