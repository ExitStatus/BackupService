using ApexCharts;
using BackupService.Authentication;
using Microsoft.AspNetCore.DataProtection;
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
            builder.Services.AddSingleton<FileSystem.IBackupFileSystem, FileSystem.BackupFileSystem>();
            builder.Services.AddSingleton<FileSystem.IEndpointFileSystemFactory, FileSystem.EndpointFileSystemFactory>();
            builder.Services.AddSingleton<Profiles.IProfileService, Profiles.ProfileService>();
            builder.Services.AddSingleton<Profiles.IFolderPairService, Profiles.FolderPairService>();
            builder.Services.AddSingleton<Profiles.IInstantSyncItemService, Profiles.InstantSyncItemService>();
            builder.Services.AddSingleton<Profiles.IArchiveSyncItemService, Profiles.ArchiveSyncItemService>();
            builder.Services.AddSingleton<Profiles.IProfileStatusService, Profiles.ProfileStatusService>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<Logging.ILogWatcher, Logging.LogWatcher>();
            builder.Services.AddSingleton<Logging.ILogRetentionService, Logging.LogRetentionService>();
            builder.Services.AddSingleton<Logging.IOperationLogFactory, Logging.OperationLogFactory>();
            builder.Services.AddSingleton<Logging.IOperationLogService, Logging.OperationLogService>();
            builder.Services.AddSingleton<Dashboard.IDashboardService, Dashboard.DashboardService>();

            // Connections (remote resources, e.g. SMB shares). SMB passwords are encrypted at rest via
            // ASP.NET Core Data Protection (cross-platform). The key ring is persisted under the app's data
            // directory with a fixed application name so secrets stay decryptable across restarts.
            builder.Services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Database.BackupDatabaseLocation.GetDataDirectory(), "keys")))
                .SetApplicationName("BackupService");
            builder.Services.AddSingleton<Security.ISecretProtector, Security.DataProtectionSecretProtector>();
            // Reads legacy (DPAPI) passwords for the one-time startup migration to Data Protection.
            builder.Services.AddSingleton<Security.ILegacySecretReader, Security.DpapiLegacySecretReader>();
            builder.Services.AddSingleton<Connections.ConnectionSecretMigrator>();
            builder.Services.AddSingleton<Connections.IConnectionService, Connections.ConnectionService>();
            builder.Services.AddSingleton<Connections.IConnectionResolver, Connections.ConnectionResolver>();
            builder.Services.AddSingleton<Connections.Smb.ISmbConnector, Connections.Smb.SmbConnector>();

            // Backup scheduling: per-type handlers, the dispatcher, and the scheduler itself.
            // The scheduler is a single instance shared across its three roles (singleton,
            // IBackupScheduler re-sync API, and the hosted background service).
            builder.Services.AddSingleton<Scheduling.IFolderPairSynchronizer, Scheduling.FolderPairSynchronizer>();
            builder.Services.AddSingleton<Scheduling.IInstantSyncProcessor, Scheduling.InstantSyncProcessor>();
            builder.Services.AddSingleton<Scheduling.IArchiveSyncProcessor, Scheduling.ArchiveSyncProcessor>();
            builder.Services.AddSingleton<Scheduling.IBackupRunRecorder, Scheduling.BackupRunRecorder>();
            builder.Services.AddSingleton<Scheduling.IProfileTypeHandler, Scheduling.FolderPairHandler>();
            builder.Services.AddSingleton<Scheduling.IProfileTypeHandler, Scheduling.InstantSyncHandler>();
            builder.Services.AddSingleton<Scheduling.IProfileTypeHandler, Scheduling.ArchiveSyncHandler>();
            builder.Services.AddSingleton<Scheduling.IBackupRunner, Scheduling.BackupRunner>();
            builder.Services.AddSingleton<Scheduling.BackupSchedulerService>();
            builder.Services.AddSingleton<Scheduling.IBackupScheduler>(sp => sp.GetRequiredService<Scheduling.BackupSchedulerService>());

            // The instant-sync watcher service is shared across its three roles (singleton,
            // IInstantSyncManager re-sync API, and the hosted background service).
            builder.Services.AddSingleton<Scheduling.InstantSyncWatcherService>();
            builder.Services.AddSingleton<Scheduling.IInstantSyncManager>(sp => sp.GetRequiredService<Scheduling.InstantSyncWatcherService>());

            // ApexCharts (Blazor-ApexCharts) for the dashboard charts.
            builder.Services.AddApexCharts();

            // Blazor Server (interactive server-side rendering).
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // The scheduler and the instant-sync watcher both run as background services.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduling.BackupSchedulerService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Scheduling.InstantSyncWatcherService>());

            var app = builder.Build();

            // Apply any pending EF Core migrations on startup.
            using (var db = app.Services.GetRequiredService<IDatabaseContextFactory>().CreateDbContext())
            {
                db.Database.Migrate();
            }

            // Migrate any legacy (DPAPI) SMB passwords to the cross-platform Data Protection format.
            app.Services.GetRequiredService<Connections.ConnectionSecretMigrator>().Migrate();

            // Ensure the default admin credential exists.
            app.Services.GetRequiredService<IAdminCredentialService>()
                .EnsureSeededAsync().GetAwaiter().GetResult();

            // Every profile starts Idle in the in-memory status tracker (status is not persisted).
            var statusService = app.Services.GetRequiredService<Profiles.IProfileStatusService>();
            foreach (var summary in app.Services.GetRequiredService<Profiles.IProfileService>()
                         .GetSummariesAsync().GetAwaiter().GetResult())
            {
                statusService.Set(summary.Id, Enumerations.ProfileStatus.Idle);
            }

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();

            // Fingerprinted static assets (referenced via @Assets["app.css"] in App.razor)
            // so CSS/JS changes bust the browser cache automatically.
            app.MapStaticAssets();

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
