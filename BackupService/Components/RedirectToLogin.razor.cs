using Microsoft.AspNetCore.Components;

namespace BackupService.Components
{
    public partial class RedirectToLogin : ComponentBase
    {
        [Inject]
        private NavigationManager Navigation { get; set; } = default!;

        protected override void OnInitialized()
        {
            var returnUrl = Navigation.ToBaseRelativePath(Navigation.Uri);
            var target = string.IsNullOrEmpty(returnUrl)
                ? "/login"
                : $"/login?returnUrl={Uri.EscapeDataString("/" + returnUrl)}";

            Navigation.NavigateTo(target, forceLoad: true);
        }
    }
}
