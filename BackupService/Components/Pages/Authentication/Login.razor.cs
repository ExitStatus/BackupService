using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BackupService.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Authentication
{
    public partial class Login : ComponentBase
    {
        [Inject]
        private IAdminCredentialService CredentialService { get; set; } = default!;

        [Inject]
        private NavigationManager Navigation { get; set; } = default!;

        // Cascaded by the framework during static server-side rendering; used to
        // write the authentication cookie on the form POST.
        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel? Input { get; set; }

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        private string? ErrorMessage { get; set; }

        protected override void OnInitialized() => Input ??= new();

        private async Task LoginAsync()
        {
            var input = Input!; // non-null after OnInitialized / form binding

            if (!await CredentialService.VerifyAsync(input.Username, input.Password))
            {
                ErrorMessage = "Invalid username or password.";
                return;
            }

            var claims = new[] { new Claim(ClaimTypes.Name, input.Username) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            // Only redirect to local URLs to avoid open-redirect attacks.
            var destination = !string.IsNullOrEmpty(ReturnUrl)
                && Uri.IsWellFormedUriString(ReturnUrl, UriKind.Relative)
                ? ReturnUrl
                : "/";

            Navigation.NavigateTo(destination);
        }

        public sealed class InputModel
        {
            [Required]
            public string Username { get; set; } = string.Empty;

            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;
        }
    }
}
