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

        private static readonly DayOfWeek[] WeekDays =
        [
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
        ];

        private string TimeText => $"{_def.Hour:00}:{_def.Minute:00}";

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

        private void OnTimeChanged(ChangeEventArgs e)
        {
            if (TimeOnly.TryParse(e.Value?.ToString(), out var time))
            {
                _def.Hour = time.Hour;
                _def.Minute = time.Minute;
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

        private async Task ApplyAsync() => await OnApply.InvokeAsync(_def);

        private static ScheduleDefinition Clone(ScheduleDefinition source) => new()
        {
            Mode = source.Mode,
            IntervalMinutes = source.IntervalMinutes,
            Minute = source.Minute,
            Hour = source.Hour,
            DaysOfWeek = new HashSet<DayOfWeek>(source.DaysOfWeek),
            DayOfMonth = source.DayOfMonth,
        };
    }
}
