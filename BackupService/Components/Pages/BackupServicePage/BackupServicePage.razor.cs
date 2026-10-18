using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class BackupServicePage : ComponentBase
    {
        // Which section is shown; defaults to the first sidebar item.
        private string? _selectedKey = "dashboard";

        // The log the Logs section should open on (expanded, scrolled to, highlighted) — set when a Profiles
        // Last Status chip is clicked, and cleared by any sidebar navigation so a later visit opens normally.
        private int? _focusLogId;

        private void OnSectionChanged(string key)
        {
            _focusLogId = null;
            _selectedKey = key;
        }

        private void OpenLog(int logId)
        {
            _focusLogId = logId;
            _selectedKey = "logs";
        }
    }
}
