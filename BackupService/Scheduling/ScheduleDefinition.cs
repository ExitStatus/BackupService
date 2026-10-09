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
        /// <summary>
        /// The intervals offered for <see cref="ScheduleMode.EveryNMinutes"/>: the ones that divide the hour. Cron
        /// restarts the count every hour, so any other interval isn't even — "*/45" runs at :00 and :45.
        /// </summary>
        public static IReadOnlyList<int> EvenIntervals { get; } = [1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30];

        public ScheduleMode Mode { get; set; } = ScheduleMode.Daily;

        /// <summary>For <see cref="ScheduleMode.EveryNMinutes"/> — one of <see cref="EvenIntervals"/>.</summary>
        public int IntervalMinutes { get; set; } = 15;

        /// <summary>Minute of the hour (0–59) for hourly/daily/weekly/monthly.</summary>
        public int Minute { get; set; }

        /// <summary>Hour of day (0–23) for daily/weekly/monthly.</summary>
        public int Hour { get; set; } = 2;

        /// <summary>Selected days for <see cref="ScheduleMode.Weekly"/>.</summary>
        public HashSet<DayOfWeek> DaysOfWeek { get; set; } = [DayOfWeek.Monday];

        /// <summary>Day of month (1–31) for <see cref="ScheduleMode.Monthly"/>; a month without that day is skipped.</summary>
        public int DayOfMonth { get; set; } = 1;

        /// <summary>For <see cref="ScheduleMode.Monthly"/>: run on the last day of every month (<see cref="DayOfMonth"/> is then ignored).</summary>
        public bool LastDayOfMonth { get; set; }

        private static readonly string[] ShortDayNames =
            ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

        public string ToCron() => Mode switch
        {
            ScheduleMode.EveryNMinutes => $"*/{IntervalMinutes} * * * *",
            ScheduleMode.Hourly => $"{Minute} * * * *",
            ScheduleMode.Daily => $"{Minute} {Hour} * * *",
            ScheduleMode.Weekly => $"{Minute} {Hour} * * {WeeklyCronDays()}",
            ScheduleMode.Monthly => $"{Minute} {Hour} {(LastDayOfMonth ? "L" : DayOfMonth.ToString(CultureInfo.InvariantCulture))} * *",
            _ => throw new InvalidOperationException($"Unknown schedule mode '{Mode}'."),
        };

        public string ToHumanReadable() => Mode switch
        {
            ScheduleMode.EveryNMinutes => IntervalMinutes == 1 ? "Every minute"
                : 60 % IntervalMinutes == 0 ? $"Every {IntervalMinutes} minutes"
                // An uneven interval saved before they were ruled out: say what it really does.
                : $"Every hour at minutes {string.Join(", ", MinutesOfHour(IntervalMinutes))}",
            ScheduleMode.Hourly => $"Every hour at minute {Minute:00}",
            ScheduleMode.Daily => $"Every day at {FormatTime(Hour, Minute)}",
            ScheduleMode.Weekly => $"Every {WeeklyHumanDays()} at {FormatTime(Hour, Minute)}",
            ScheduleMode.Monthly => LastDayOfMonth
                ? $"On the last day of every month at {FormatTime(Hour, Minute)}"
                : DayOfMonth > 28
                    ? $"On day {DayOfMonth} of each month that has one at {FormatTime(Hour, Minute)}"
                    : $"On day {DayOfMonth} of every month at {FormatTime(Hour, Minute)}",
            _ => string.Empty,
        };

        /// <summary>
        /// Why this schedule can't be saved, or null when it can. A cron that doesn't parse would be dropped by the
        /// scheduler without a word, so nothing invalid may be stored.
        /// </summary>
        public string? GetValidationError()
        {
            switch (Mode)
            {
                case ScheduleMode.EveryNMinutes:
                    if (!EvenIntervals.Contains(IntervalMinutes))
                    {
                        return "Choose an interval that divides the hour evenly.";
                    }
                    break;

                case ScheduleMode.Hourly:
                    if (Minute is < 0 or > 59)
                    {
                        return "The minute must be between 0 and 59.";
                    }
                    break;

                case ScheduleMode.Weekly when DaysOfWeek.Count == 0:
                    return "Choose at least one day.";

                case ScheduleMode.Monthly when !LastDayOfMonth && DayOfMonth is < 1 or > 31:
                    return "The day of the month must be between 1 and 31.";
            }

            if (Mode is ScheduleMode.Daily or ScheduleMode.Weekly or ScheduleMode.Monthly
                && (Hour is < 0 or > 23 || Minute is < 0 or > 59))
            {
                return "Choose a valid time.";
            }

            return CronExpression.TryParse(ToCron(), out _) ? null : "This schedule isn't valid.";
        }

        /// <summary>Throws <see cref="CronFormatException"/> if the produced cron is invalid.</summary>
        public CronExpression Validate() => CronExpression.Parse(ToCron());

        /// <summary>
        /// Parses a cron string produced by <see cref="ToCron"/> back into a definition, or
        /// null when the string is null/empty, not one of the recognised forms, or out of range.
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
                if (int.TryParse(minute.AsSpan(2), out var n) && n is >= 1 and <= 59
                    && hour == "*" && dom == "*" && month == "*" && dow == "*")
                {
                    return new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = n };
                }
                return null;
            }

            if (!int.TryParse(minute, out var min) || min is < 0 or > 59)
            {
                return null;
            }

            // Hourly: M * * * *
            if (hour == "*" && dom == "*" && month == "*" && dow == "*")
            {
                return new ScheduleDefinition { Mode = ScheduleMode.Hourly, Minute = min };
            }

            if (!int.TryParse(hour, out var hr) || hr is < 0 or > 23)
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

            // Monthly: M H DOM * *, or M H L * * for the last day
            if (month == "*" && dow == "*" && dom == "L")
            {
                return new ScheduleDefinition { Mode = ScheduleMode.Monthly, Minute = min, Hour = hr, LastDayOfMonth = true };
            }
            if (month == "*" && dow == "*" && int.TryParse(dom, out var domValue))
            {
                return domValue is < 1 or > 31
                    ? null
                    : new ScheduleDefinition { Mode = ScheduleMode.Monthly, Minute = min, Hour = hr, DayOfMonth = domValue };
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
        /// "Not scheduled" when empty, a warning when it can't run (the scheduler skips a cron it can't parse), the
        /// friendly description when recognised, and the raw cron only as a last resort for an unrecognised form.
        /// </summary>
        public static string Describe(string? cron)
        {
            if (string.IsNullOrWhiteSpace(cron))
            {
                return "Not scheduled";
            }
            if (!CronExpression.TryParse(cron, out _))
            {
                return "Invalid schedule — not running";
            }
            return FromCron(cron)?.ToHumanReadable() ?? cron;
        }

        /// <summary>12-hour time with AM/PM (e.g. "05:00 AM"), matching the time picker.</summary>
        private static string FormatTime(int hour, int minute) =>
            new TimeOnly(Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59)).ToString("hh:mm tt", CultureInfo.InvariantCulture);

        // The minutes past each hour "*/N" fires at.
        private static IEnumerable<string> MinutesOfHour(int interval)
        {
            for (var minute = 0; minute < 60; minute += interval)
            {
                yield return minute.ToString("00", CultureInfo.InvariantCulture);
            }
        }

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
