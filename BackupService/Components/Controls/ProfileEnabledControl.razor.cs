using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Shows a profile's enabled state as a sliding <see cref="ToggleButton"/> plus a status
    /// swatch (green when enabled, red when disabled). Two-way bindable via <c>@bind-Value</c>;
    /// raises <see cref="ValueChanged"/> when toggled so the parent can persist the change.
    /// </summary>
    public partial class ProfileEnabledControl : ComponentBase
    {
        [Parameter]
        public bool Value { get; set; }

        [Parameter]
        public EventCallback<bool> ValueChanged { get; set; }

        [Parameter]
        public bool Disabled { get; set; }

        private async Task OnToggle(bool value)
        {
            Value = value;
            await ValueChanged.InvokeAsync(value);
        }
    }
}
