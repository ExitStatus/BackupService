using System.ComponentModel.DataAnnotations;
using BackupService.Authentication;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Authentication
{
    /// <summary>
    /// Self-contained modal for changing the admin password. Validates that the new
    /// password is supplied twice and matches, then asks the service to verify the
    /// current password and persist the change.
    /// </summary>
    public partial class ChangePasswordDialog : ComponentBase
    {
        [Inject]
        private IAdminCredentialService CredentialService { get; set; } = default!;

        [Parameter]
        public EventCallback OnCancel { get; set; }

        [Parameter]
        public EventCallback OnSaved { get; set; }

        private InputModel Input { get; set; } = new();

        private string? ErrorMessage { get; set; }

        private async Task SubmitAsync()
        {
            ErrorMessage = null;

            var changed = await CredentialService.ChangePasswordAsync(
                Input.CurrentPassword, Input.NewPassword);

            if (!changed)
            {
                ErrorMessage = "Current password is incorrect.";
                return;
            }

            await OnSaved.InvokeAsync();
        }

        public sealed class InputModel
        {
            [Required]
            public string CurrentPassword { get; set; } = string.Empty;

            [Required]
            public string NewPassword { get; set; } = string.Empty;

            [Required]
            [Compare(nameof(NewPassword), ErrorMessage = "The new passwords do not match.")]
            public string ConfirmPassword { get; set; } = string.Empty;
        }
    }
}
