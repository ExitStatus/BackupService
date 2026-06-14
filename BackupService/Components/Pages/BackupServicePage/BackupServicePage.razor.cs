using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class BackupServicePage : ComponentBase
    {
        // Which section is shown; defaults to the first sidebar item.
        private string? _selectedKey = "dashboard";
    }
}
