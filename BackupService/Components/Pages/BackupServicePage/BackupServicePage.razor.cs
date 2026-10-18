using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class BackupServicePage : ComponentBase
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = default!;

        // The section lives in the URL (/?section=logs) so each switch is a browser history entry — Back returns to
        // the previous section instead of leaving the app (to the login page it was entered from).
        [SupplyParameterFromQuery(Name = "section")]
        public string? Section { get; set; }

        // The log the Logs section should open on (expanded, scrolled to, highlighted) — set when a Profiles Last
        // Status chip is clicked (/?section=logs&log=123). A plain sidebar navigation drops it.
        [SupplyParameterFromQuery(Name = "log")]
        public int? FocusLogId { get; set; }

        // Which section is shown; defaults to the first sidebar item.
        private string SelectedKey => string.IsNullOrEmpty(Section) ? "dashboard" : Section;

        private void OnSectionChanged(string key) =>
            Navigation.NavigateTo($"/?section={Uri.EscapeDataString(key)}");

        private void OpenLog(int logId) =>
            Navigation.NavigateTo($"/?section=logs&log={logId}");
    }
}
