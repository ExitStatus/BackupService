using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A small horizontal tab strip (the section-switcher pattern used by <c>SettingsSidepanel</c>, laid
    /// out horizontally). Two-way bindable via <c>@bind-SelectedKey</c>; the parent switches the shown
    /// content on the selected key.
    /// </summary>
    public partial class TabBar : ComponentBase
    {
        [Parameter]
        public IReadOnlyList<TabItem> Items { get; set; } = [];

        [Parameter]
        public string? SelectedKey { get; set; }

        [Parameter]
        public EventCallback<string> SelectedKeyChanged { get; set; }

        private async Task SelectAsync(string key)
        {
            if (key == SelectedKey)
            {
                return;
            }

            SelectedKey = key;
            await SelectedKeyChanged.InvokeAsync(key);
        }

        public sealed record TabItem(string Key, string Label);
    }
}
