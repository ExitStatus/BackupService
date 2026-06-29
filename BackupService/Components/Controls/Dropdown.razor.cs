using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A theme-controlled dropdown that replaces the native <c>&lt;select&gt;</c> (whose
    /// OS-drawn popup ignores the dark theme on Windows). Supports two-way binding via
    /// <c>@bind-Value</c>. Closes when an option is clicked or you click outside it (a global handler in App.razor).
    /// </summary>
    public partial class Dropdown<TValue> : ComponentBase
    {
        [Parameter]
        public TValue Value { get; set; } = default!;

        [Parameter]
        public EventCallback<TValue> ValueChanged { get; set; }

        [Parameter, EditorRequired]
        public IEnumerable<TValue> Items { get; set; } = [];

        [Parameter, EditorRequired]
        public Func<TValue, string> DisplayText { get; set; } = value => value?.ToString() ?? string.Empty;

        [Parameter]
        public bool Disabled { get; set; }

        private bool _open;

        private void Toggle()
        {
            if (!Disabled)
            {
                _open = !_open;
            }
        }

        private async Task SelectAsync(TValue item)
        {
            _open = false;

            if (!EqualityComparer<TValue>.Default.Equals(item, Value))
            {
                Value = item;
                await ValueChanged.InvokeAsync(item);
            }
        }
    }
}
