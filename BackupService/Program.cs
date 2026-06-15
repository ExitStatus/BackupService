using BackupService.Authentication;
using BackupService.Components;
using BackupService.Database;
using BackupService.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace BackupService
{
    public class Program
    {
        public static int Main(string[] args)
        {
            // Service install/uninstall commands run and exit without starting the host.
            if (args.Contains("--install"))
            {
                return WindowsServiceInstaller.Install();
            }
            if (args.Contains("--uninstall"))
            {
                return WindowsServiceInstaller.Uninstall();
            }

            var builder = WebApplication.CreateBuilder(args);

            // Allow the host to run under the Windows Service Control Manager,
            // while still behaving normally when launched from the console.
            builder.Host.UseWindowsService();

            // Entity Framework Core, backed by a single SQLite database. The
            // context is not registered directly; code resolves the factory and
            // creates a short-lived context per unit of work.
            builder.Services.AddSingleton<IDatabaseContextFactory, DatabaseContextFactory>();

            // Cookie authentication for the single admin account. The session
            // expires after 30 minutes of inactivity (sliding renewal).
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/login";
                    options.AccessDeniedPath = "/login";
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                    options.SlidingExpiration = true;
                });

            // Pages are protected with [Authorize] (applied to every page via
            // Components/Pages/_Imports.razor) and enforced by AuthorizeRouteView;
            // the login page opts out with [AllowAnonymous]. A global fallback
            // policy is intentionally NOT used: it applies at the Blazor endpoint
            // level and would also block the /login route, causing a redirect loop.
            builder.Services.AddAuthorization();

            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddSingleton<IAdminCredentialService, AdminCredentialService>();
            builder.Services.AddSingleton<IAuthenticationHistoryService, AuthenticationHistoryService>();
            builder.Services.AddSingleton<FileSystem.IFolderBrowser, FileSystem.FolderBrowser>();
            builder.Services.AddSingleton<Profiles.IProfileService, Profiles.ProfileService>();

            // Blazor Server (interactive server-side rendering).
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // Background backup worker.
            builder.Services.AddHostedService<Worker>();

            var app = builder.Build();

            // Apply any pending EF Core migrations on startup.
            using (var db = app.Services.GetRequiredService<IDatabaseContextFactory>().CreateDbContext())
            {
                db.Database.Migrate();
            }

            // Ensure the default admin credential exists.
            app.Services.GetRequiredService<IAdminCredentialService>()
                .EnsureSeededAsync().GetAwaiter().GetResult();

            app.UseStaticFiles();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            // Sign the admin out and return to the login page.
            app.MapPost("/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login");
            });

            app.Run();
            return 0;
        }
    }
}
