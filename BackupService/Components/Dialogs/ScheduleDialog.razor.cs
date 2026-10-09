using BackupService.Enumerations;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Modal that builds a <see cref="ScheduleDefinition"/> from friendly controls. The
    /// raw cron string is never shown; a human-readable preview is displayed instead.
    /// </summary>
    public partial class ScheduleDialog : ComponentBase
    {
        [Parameter]
        public ScheduleDefinition? Initial { get; set; }

        [Parameter]
        public EventCallback<ScheduleDefinition> OnApply { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        private ScheduleDefinition _def = new();
        private string? _error;

        // The day picker's value for "Last day of the month".
        private const int LastDay = 0;

        private static readonly DayOfWeek[] WeekDays =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        ];

        private static readonly int[] MonthDays = [.. Enumerable.Range(1, 31), LastDay];

        // Clamped: the hourly minute box can hold anything typed into it, and TimeOnly throws on an out-of-range
        // minute — switching from Hourly to Daily with 75 in the box would otherwise crash the page.
        private TimeOnly ScheduleTime
        {
            get => new(Math.Clamp(_def.Hour, 0, 23), Math.Clamp(_def.Minute, 0, 59));
            set
            {
                _def.Hour = value.Hour;
                _def.Minute = value.Minute;
            }
        }

        // The intervals that divide the hour, plus an uneven one saved before they were ruled out (so it still shows,
        // with the validation message asking for another).
        private IReadOnlyList<int> IntervalOptions =>
            ScheduleDefinition.EvenIntervals.Contains(_def.IntervalMinutes)
                ? ScheduleDefinition.EvenIntervals
                : [.. ScheduleDefinition.EvenIntervals.Append(_def.IntervalMinutes).Order()];

        private int MonthDay
        {
            get => _def.LastDayOfMonth ? LastDay : _def.DayOfMonth;
            set
            {
                _def.LastDayOfMonth = value == LastDay;
                if (value != LastDay)
                {
                    _def.DayOfMonth = value;
                }
            }
        }

        private static string IntervalLabel(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

        private static string MonthDayLabel(int day) => day == LastDay ? "Last day of the month" : day.ToString();

        private static string ModeLabel(ScheduleMode mode) => mode switch
        {
            ScheduleMode.EveryNMinutes => "Every N minutes",
            ScheduleMode.Hourly => "Hourly",
            ScheduleMode.Daily => "Daily",
            ScheduleMode.Weekly => "Weekly",
            ScheduleMode.Monthly => "Monthly",
            _ => mode.ToString(),
        };

        protected override void OnInitialized()
        {
            // Edit a copy so Cancel discards changes.
            if (Initial is not null)
            {
                _def = Clone(Initial);
            }
        }

        private void ToggleDay(DayOfWeek day, bool selected)
        {
            if (selected)
            {
                _def.DaysOfWeek.Add(day);
            }
            else
            {
                _def.DaysOfWeek.Remove(day);
            }
        }

        private async Task ApplyAsync()
        {
            // An invalid cron would be saved and then silently never run, so it's refused here, where it's chosen.
            _error = _def.GetValidationError();
            if (_error is null)
            {
                await OnApply.InvokeAsync(_def);
            }
        }

        private static ScheduleDefinition Clone(ScheduleDefinition source) => new()
        {
            Mode = source.Mode,
            IntervalMinutes = source.IntervalMinutes,
            Minute = source.Minute,
            Hour = source.Hour,
            DaysOfWeek = new HashSet<DayOfWeek>(source.DaysOfWeek),
            DayOfMonth = source.DayOfMonth,
            LastDayOfMonth = source.LastDayOfMonth,
        };
    }
}
