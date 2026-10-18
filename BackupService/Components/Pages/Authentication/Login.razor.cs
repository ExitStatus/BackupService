using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BackupService.Authentication;
using BackupService.Database;
using BackupService.Enumerations;
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
        private IAuthenticationHistoryService History { get; set; } = default!;

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

        protected override void OnInitialized()
        {
            Input ??= new();

            // Never served from the browser cache (or back/forward cache): pressing Back past the app's history lands
            // here, and must hit the server so the check below can run.
            HttpContext.Response.Headers.CacheControl = "no-store";

            // Already signed in (typically Back from the home page) — there's nothing to do here, so go back in.
            if (HttpMethods.IsGet(HttpContext.Request.Method) && HttpContext.User.Identity?.IsAuthenticated == true)
            {
                Navigation.NavigateTo(Destination());
            }
        }

        private async Task LoginAsync()
        {
            var input = Input!; // non-null after OnInitialized / form binding

            if (!await CredentialService.VerifyAsync(input.Username, input.Password))
            {
                await History.RecordAsync(AuthenticationEventType.LoginFailed);
                ErrorMessage = "Invalid username or password.";
                return;
            }

            await History.RecordAsync(AuthenticationEventType.LoginSucceeded);

            var claims = new[] { new Claim(ClaimTypes.Name, input.Username) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));

            Navigation.NavigateTo(Destination());
        }

        // Only redirect to local paths to avoid open-redirect attacks. "//host" and "/\host" are relative URIs too, but
        // browsers read them as another site — and a signed-in user is now redirected without a click, so they matter.
        private string Destination() =>
            IsLocalPath(ReturnUrl) ? ReturnUrl! : "/";

        internal static bool IsLocalPath(string? url) =>
            !string.IsNullOrEmpty(url)
            && url[0] == '/'
            && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            && Uri.IsWellFormedUriString(url, UriKind.Relative);

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
