using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Settings
{
    public partial class Settings : ComponentBase
    {
        // Which settings section is shown; defaults to the first panel item.
        private string? _selectedKey = "authentication";
    }
}
