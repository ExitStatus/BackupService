using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A sliding on/off switch: the knob sits left for <c>false</c> and right for <c>true</c>.
    /// Supports two-way binding via <c>@bind-Value</c> (the project's Value/ValueChanged
    /// convention, see <see cref="Dropdown{TValue}"/>).
    /// </summary>
    public partial class ToggleButton : ComponentBase
    {
        [Parameter]
        public bool Value { get; set; }

        [Parameter]
        public EventCallback<bool> ValueChanged { get; set; }

        [Parameter]
        public bool Disabled { get; set; }

        private async Task ToggleAsync()
        {
            if (Disabled)
            {
                return;
            }

            Value = !Value;
            await ValueChanged.InvokeAsync(Value);
        }
    }
}
