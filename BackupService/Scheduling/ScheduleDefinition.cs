using System.Globalization;
using BackupService.Enumerations;
using Cronos;

namespace BackupService.Scheduling
{
    /// <summary>
    /// A user-friendly schedule selection. Converts to a standard 5-field cron string
    /// (stored internally) and a human-readable label (shown to the user). The raw cron
    /// is never surfaced in the UI.
    /// </summary>
    public sealed class ScheduleDefinition
    {
        public ScheduleMode Mode { get; set; } = ScheduleMode.Daily;

        /// <summary>For <see cref="ScheduleMode.EveryNMinutes"/> (1–59).</summary>
        public int IntervalMinutes { get; set; } = 15;

        /// <summary>Minute of the hour (0–59) for hourly/daily/weekly/monthly.</summary>
        public int Minute { get; set; }

        /// <summary>Hour of day (0–23) for daily/weekly/monthly.</summary>
        public int Hour { get; set; } = 2;

        /// <summary>Selected days for <see cref="ScheduleMode.Weekly"/>.</summary>
        public HashSet<DayOfWeek> DaysOfWeek { get; set; } = [DayOfWeek.Monday];

        /// <summary>Day of month (1–31) for <see cref="ScheduleMode.Monthly"/>.</summary>
        public int DayOfMonth { get; set; } = 1;

        private static readonly string[] ShortDayNames =
            ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

        public string ToCron() => Mode switch
        {
            ScheduleMode.EveryNMinutes => $"*/{IntervalMinutes} * * * *",
            ScheduleMode.Hourly => $"{Minute} * * * *",
            ScheduleMode.Daily => $"{Minute} {Hour} * * *",
            ScheduleMode.Weekly => $"{Minute} {Hour} * * {WeeklyCronDays()}",
            ScheduleMode.Monthly => $"{Minute} {Hour} {DayOfMonth} * *",
            _ => throw new InvalidOperationException($"Unknown schedule mode '{Mode}'."),
        };

        public string ToHumanReadable() => Mode switch
        {
            ScheduleMode.EveryNMinutes =>
                IntervalMinutes == 1 ? "Every minute" : $"Every {IntervalMinutes} minutes",
            ScheduleMode.Hourly => $"Every hour at minute {Minute:00}",
            ScheduleMode.Daily => $"Every day at {FormatTime(Hour, Minute)}",
            ScheduleMode.Weekly => $"Every {WeeklyHumanDays()} at {FormatTime(Hour, Minute)}",
            ScheduleMode.Monthly => $"On day {DayOfMonth} of every month at {FormatTime(Hour, Minute)}",
            _ => string.Empty,
        };

        /// <summary>Throws <see cref="CronFormatException"/> if the produced cron is invalid.</summary>
        public CronExpression Validate() => CronExpression.Parse(ToCron());

        /// <summary>
        /// Parses a cron string produced by <see cref="ToCron"/> back into a definition, or
        /// null when the string is null/empty or not one of the recognised forms.
        /// </summary>
        public static ScheduleDefinition? FromCron(string? cron)
        {
            if (string.IsNullOrWhiteSpace(cron))
            {
                return null;
            }

            var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5)
            {
                return null;
            }

            string minute = parts[0], hour = parts[1], dom = parts[2], month = parts[3], dow = parts[4];

            // Every N minutes: */N * * * *
            if (minute.StartsWith("*/", StringComparison.Ordinal))
            {
                if (int.TryParse(minute.AsSpan(2), out var n)
                    && hour == "*" && dom == "*" && month == "*" && dow == "*")
                {
                    return new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = n };
                }
                return null;
            }

            if (!int.TryParse(minute, out var min))
            {
                return null;
            }

            // Hourly: M * * * *
            if (hour == "*" && dom == "*" && month == "*" && dow == "*")
            {
                return new ScheduleDefinition { Mode = ScheduleMode.Hourly, Minute = min };
            }

            if (!int.TryParse(hour, out var hr))
            {
                return null;
            }

            // Weekly: M H * * d[,d...]
            if (dom == "*" && month == "*" && dow != "*")
            {
                var days = new HashSet<DayOfWeek>();
                foreach (var token in dow.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(token, out var d) || d < 0 || d > 6)
                    {
                        return null;
                    }
                    days.Add((DayOfWeek)d);
                }

                return days.Count == 0
                    ? null
                    : new ScheduleDefinition { Mode = ScheduleMode.Weekly, Minute = min, Hour = hr, DaysOfWeek = days };
            }

            // Monthly: M H DOM * *
            if (month == "*" && dow == "*" && int.TryParse(dom, out var domValue))
            {
                return new ScheduleDefinition { Mode = ScheduleMode.Monthly, Minute = min, Hour = hr, DayOfMonth = domValue };
            }

            // Daily: M H * * *
            if (dom == "*" && month == "*" && dow == "*")
            {
                return new ScheduleDefinition { Mode = ScheduleMode.Daily, Minute = min, Hour = hr };
            }

            return null;
        }

        /// <summary>
        /// Human-readable label for a stored cron string — always safe to show the user:
        /// "Not scheduled" when empty, the friendly description when recognised, and the raw
        /// cron only as a last resort for an unrecognised form.
        /// </summary>
        public static string Describe(string? cron) =>
            string.IsNullOrWhiteSpace(cron)
                ? "Not scheduled"
                : FromCron(cron)?.ToHumanReadable() ?? cron;

        /// <summary>12-hour time with AM/PM (e.g. "05:00 AM"), matching the time picker.</summary>
        private static string FormatTime(int hour, int minute) =>
            new TimeOnly(hour, minute).ToString("hh:mm tt", CultureInfo.InvariantCulture);

        private string WeeklyCronDays()
        {
            var days = DaysOfWeek.Count == 0 ? [DayOfWeek.Monday] : DaysOfWeek;
            return string.Join(",", days.OrderBy(d => (int)d).Select(d => (int)d));
        }

        private string WeeklyHumanDays()
        {
            var days = DaysOfWeek.Count == 0 ? [DayOfWeek.Monday] : DaysOfWeek;
            return string.Join(", ", days.OrderBy(d => (int)d).Select(d => ShortDayNames[(int)d]));
        }
    }
}
