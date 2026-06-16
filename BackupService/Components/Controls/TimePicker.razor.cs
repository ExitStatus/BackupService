using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Themed clock-face time picker (Material-style). The trigger shows the current time;
    /// clicking opens a popup with a 12-hour clock you click to set the hour (which then
    /// advances to the minute) plus an AM/PM toggle and a clickable HH:MM header. Two-way
    /// bindable via <c>@bind-Value</c> (a <see cref="TimeOnly"/>); applies on OK.
    /// </summary>
    public partial class TimePicker : ComponentBase
    {
        private enum Field
        {
            Hour,
            Minute,
        }

        [Parameter]
        public TimeOnly Value { get; set; }

        [Parameter]
        public EventCallback<TimeOnly> ValueChanged { get; set; }

        [Parameter]
        public bool Disabled { get; set; }

        // Clock geometry — must match the .tp-clock size in app.css (240px face, centre 120).
        private const double Centre = 120;
        private const double NumberRadius = 92;

        private bool _open;
        private Field _field = Field.Hour;
        private int _hour;   // 0-23 working copy
        private int _minute; // 0-59 working copy

        private static readonly int[] MinuteMarks = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55];

        private bool IsPm => _hour >= 12;

        private int DisplayHour
        {
            get
            {
                var hour = _hour % 12;
                return hour == 0 ? 12 : hour;
            }
        }

        // The collapsed trigger reflects the bound Value (the working copy _hour/_minute is
        // only populated once the picker is opened).
        private string TriggerText
        {
            get
            {
                var hour12 = Value.Hour % 12;
                if (hour12 == 0)
                {
                    hour12 = 12;
                }

                return $"{hour12:00}:{Value.Minute:00} {(Value.Hour >= 12 ? "PM" : "AM")}";
            }
        }

        private string HeaderHour => $"{DisplayHour:00}";

        private string HeaderMinute => $"{_minute:00}";

        private string HourHandStyle => $"transform: rotate({Fmt((_hour % 12) * 30 + _minute * 0.5)}deg);";

        private string MinuteHandStyle => $"transform: rotate({Fmt(_minute * 6)}deg);";

        private double KnobAngle => _field == Field.Hour ? (_hour % 12) * 30 : _minute * 6;

        private string KnobStyle =>
            $"left: {Fmt(Centre + NumberRadius * Math.Sin(KnobAngle * Math.PI / 180))}px; " +
            $"top: {Fmt(Centre - NumberRadius * Math.Cos(KnobAngle * Math.PI / 180))}px;";

        private void Open()
        {
            if (Disabled)
            {
                return;
            }

            _hour = Value.Hour;
            _minute = Value.Minute;
            _field = Field.Hour;
            _open = true;
        }

        private void Close() => _open = false;

        private void SetField(Field field) => _field = field;

        private void SetAm()
        {
            if (IsPm)
            {
                _hour -= 12;
            }
        }

        private void SetPm()
        {
            if (!IsPm)
            {
                _hour += 12;
            }
        }

        private void OnFaceClick(MouseEventArgs e)
        {
            // Offsets are relative to the clock face (all overlay children are pointer-events:none),
            // so the click always reports a position on the 240px face.
            var dx = e.OffsetX - Centre;
            var dy = e.OffsetY - Centre;
            var degrees = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0 at 12 o'clock, clockwise
            if (degrees < 0)
            {
                degrees += 360;
            }

            if (_field == Field.Hour)
            {
                var index = (int)Math.Round(degrees / 30) % 12; // 0 == 12 o'clock
                var hour12 = index == 0 ? 12 : index;
                _hour = To24Hour(hour12, IsPm);
                _field = Field.Minute; // advance to the minute, as requested
            }
            else
            {
                _minute = (int)Math.Round(degrees / 6) % 60;
            }
        }

        private void SelectHour(int hour12)
        {
            _hour = To24Hour(hour12, IsPm);
            _field = Field.Minute; // advance to the minute, as requested
        }

        private void SelectMinute(int minute) => _minute = minute;

        private async Task ApplyAsync()
        {
            Value = new TimeOnly(_hour, _minute);
            await ValueChanged.InvokeAsync(Value);
            _open = false;
        }

        private bool IsSelectedHour(int hour12) => DisplayHour == hour12;

        private bool IsSelectedMinute(int minute) => _minute == minute;

        private static string NumberStyle(int markIndex, double anglePerMark)
        {
            var radians = markIndex * anglePerMark * Math.PI / 180;
            var x = Centre + NumberRadius * Math.Sin(radians);
            var y = Centre - NumberRadius * Math.Cos(radians);
            return $"left: {Fmt(x)}px; top: {Fmt(y)}px;";
        }

        private static int To24Hour(int hour12, bool isPm) => isPm
            ? (hour12 == 12 ? 12 : hour12 + 12)
            : (hour12 == 12 ? 0 : hour12);

        private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
