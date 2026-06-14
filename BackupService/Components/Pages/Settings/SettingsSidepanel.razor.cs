using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Settings
{
    /// <summary>
    /// Left-docked navigation panel for the settings page. Self-contained: it owns
    /// its list of sections and reports the selected one via <see cref="SelectedKey"/>
    /// (two-way bindable with <c>@bind-SelectedKey</c>).
    /// </summary>
    public partial class SettingsSidepanel : ComponentBase
    {
        [Parameter]
        public string? SelectedKey { get; set; }

        [Parameter]
        public EventCallback<string> SelectedKeyChanged { get; set; }

        private static readonly IReadOnlyList<NavItem> Items =
        [
            new NavItem("authentication", "Authentication", "Icons/key.png"),
        ];

        protected override async Task OnInitializedAsync()
        {
            // Fall back to the first section if the host didn't supply a selection.
            if (string.IsNullOrEmpty(SelectedKey) && Items.Count > 0)
            {
                await SelectAsync(Items[0].Key);
            }
        }

        private async Task SelectAsync(string key)
        {
            if (key == SelectedKey)
            {
                return;
            }

            SelectedKey = key;
            await SelectedKeyChanged.InvokeAsync(key);
        }

        private sealed record NavItem(string Key, string Label, string IconPath);
    }
}
