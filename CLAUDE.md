# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

This is a **.NET 10 ASP.NET Core app (`Microsoft.NET.Sdk.Web`) hosting Blazor Server plus a background worker**, designed to run as a **Windows Service** (`builder.Host.UseWindowsService()`). It started life as a Worker Service template and was converted: the same process now serves an interactive Blazor Server UI *and* runs `Worker` as a hosted background service. The actual backup functionality has not been implemented yet — `Worker.cs` still just logs a heartbeat — so most work here is greenfield.

## Commands

Run from the repo root (`C:\Projects\BackupService`). The solution is `BackupService.slnx` (the newer XML solution format); the project lives in `BackupService/`.

```powershell
dotnet build                          # build the solution
dotnet run --project BackupService    # run web host + worker (Ctrl+C to stop)
dotnet watch --project BackupService run   # run with hot reload
```

Running interactively (console / `dotnet run`) starts Kestrel and serves the Blazor UI. `UseWindowsService()` only changes the host lifetime when the process is actually started by the Windows SCM, so local console runs are unaffected. To install as a Windows Service, publish then run `BackupService.exe --install` from an elevated prompt (see the install commands below).

**HTTP only — deployed on `http://localhost:55000`, dev on `http://localhost:5080`.** There is no HTTPS (the admin UI is local-only). The port lives in **environment-specific** config (not base `appsettings.json`): `appsettings.Production.json` → `Kestrel:Endpoints:Http:Url` = 55000 (the deployed service runs in Production), `appsettings.Development.json` → 5080. Base `appsettings.json` intentionally has **no** `Kestrel:Endpoints` — otherwise Visual Studio reads it as the default and launches the browser on 55000 even in debug. The dev launch profile (`launchSettings.json`) sets `ASPNETCORE_ENVIRONMENT=Development` (web-standard) and `applicationUrl=5080`. Note a `Kestrel:Endpoints` value takes precedence over `ASPNETCORE_URLS`/`applicationUrl`, so the per-environment port is set via the config layer.

**Install as a Windows Service:** run the published exe elevated — `BackupService.exe --install` (registers autostart + LocalSystem, replaces an existing service, and starts it) or `BackupService.exe --uninstall` (stops + removes it). These commands run and exit without starting the web host; see `Hosting/WindowsServiceInstaller.cs` (shells out to `sc.exe`). Must be run from the published exe, not `dotnet …dll`.

**Deploy via publish profile:** `Properties/PublishProfiles/Install.pubxml` publishes to `C:\Tools\BackupService` and (re)installs the service — `dotnet publish -p:PublishProfile=Install`. It stops the running service before copying (so files aren't locked) and runs `--install` afterward. **Run it from an elevated prompt**; otherwise the deploy still succeeds but the install step only warns (it needs admin). The ASP.NET Core dev HTTPS certificate / "trust the self-signed cert" prompt is irrelevant here and does not apply to the service. If the UI ever needs network/HTTPS access, configure a real cert under `Kestrel:Endpoints` and add `CookieSecurePolicy.Always` + `UseHttpsRedirection()`.

Note when running the built dll directly for testing: set the working directory to the output folder, otherwise the content root is the current directory and `appsettings.json` (hence the Kestrel binding) won't be found. The real service is unaffected because `UseWindowsService()` sets the content root to the exe's directory.

Tests live in the sibling **`BackupService.UnitTests`** project (in the solution). Stack: **NUnit** + **Moq** + **FluentAssertions**. Note FluentAssertions is pinned to **7.x** (the last Apache-2.0/free line — 8.x requires a paid commercial license); don't bump to 8.x without a license.

```powershell
dotnet test                                                   # run all tests
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"   # single test
```

Testing services that use the DbContext factory: mock `IDatabaseContextFactory` with Moq and have `CreateDbContext()` return a `BackupDbContext` over a **shared in-memory SQLite** connection (`new SqliteConnection("DataSource=:memory:")` kept open for the test, `EnsureCreated()` for schema) — see `Authentication/AdminCredentialServiceTests.cs` for the pattern. Use `NullLogger<T>.Instance` for `ILogger` dependencies.

### Database / EF Core migrations

Migrations are kept under `BackupService/Database/Migrations`. Always pass `--output-dir Database/Migrations` when adding one so it lands there rather than the default `Migrations/` folder. The `dotnet-ef` tool version must match the EF Core packages (currently 10.0.9).

```powershell
dotnet ef migrations add <Name> --project BackupService --output-dir Database/Migrations
dotnet ef database update --project BackupService     # apply migrations manually
dotnet ef migrations remove --project BackupService   # undo the last (unapplied) migration
```

The app also calls `db.Database.Migrate()` on startup, so pending migrations apply automatically when it runs. The SQLite file (`backupservice.db`, plus `-wal`/`-shm`) is generated at the content root and is not source.

## Architecture

- `Program.cs` — host bootstrap via `WebApplication.CreateBuilder`. Calls `UseWindowsService()`, registers the DB factory, cookie auth + authorization, Razor Components with interactive server rendering (`AddRazorComponents().AddInteractiveServerComponents()`), registers `Worker` as a hosted service, then maps the Blazor endpoint (`MapRazorComponents<App>().AddInteractiveServerRenderMode()`) and a `/logout` POST endpoint. The middleware pipeline is `UseStaticFiles` → `UseAuthentication` → `UseAuthorization` → `UseAntiforgery` → endpoints. Startup also runs `db.Database.Migrate()` and `IAdminCredentialService.EnsureSeededAsync()`.
- `Worker.cs` — a `BackgroundService` with the long-running loop in `ExecuteAsync`; backup logic belongs in services injected into it (see DI convention below), not inline.
- `Database/` — **all** database code lives here: `BackupDbContext` (EF Core, SQLite), the entity models (e.g. `BackupRecord`), the `Migrations/` subfolder, `BackupDatabaseLocation`, and the `DatabaseContextFactory`. Keep new entities and DbContext changes in this folder.
  - **Factory pattern — the `DbContext` is NOT registered in DI.** Instead `IDatabaseContextFactory` (impl `DatabaseContextFactory`) is registered as a singleton. Code that needs the database injects `IDatabaseContextFactory` and calls `CreateDbContext()`, owning and disposing a short-lived context per unit of work (`using var db = factory.CreateDbContext();`). Do **not** add `AddDbContext<BackupDbContext>` back. `DatabaseContextFactory` also implements `IDesignTimeDbContextFactory<BackupDbContext>` so `dotnet ef` can build a context at design time without the context being in DI.
  - **SQLite caveat:** SQLite cannot `ORDER BY` (or compare) a `DateTimeOffset` column. For "newest first" ordering use the autoincrement `Id` (monotonic with insertion) rather than a timestamp column — see `AuthenticationHistoryService.GetPageAsync`. `DateTimeOffset` columns are fine to store and `SELECT`/display.
  - **Single database, location depends on hosting** (`BackupDatabaseLocation.GetConnectionString()`): there is only ever one SQLite file. When running as a Windows Service it lives under ProgramData (`%ProgramData%\BackupService\backupservice.db`) so it's shared machine-wide regardless of the service account; when run interactively (debugging/console) it sits next to the exe (`AppContext.BaseDirectory`). The decision uses `WindowsServiceHelpers.IsWindowsService()`. There is no connection string in `appsettings.json` — it is computed in code.
- `Components/` — the Blazor Server UI. `App.razor` is the root HTML document (loads `_framework/blazor.web.js` and the theme `app.css`), `Routes.razor` wires the router via `AuthorizeRouteView` (unauthenticated → `RedirectToLogin`), and routable pages live in `Components/Pages/` (`@page` directives). `_Imports.razor` holds shared `@using`s for the component tree. Two layouts: `Components/Layout/MainLayout.razor` (default — top bar with signed-in user + Log out, gated by `<AuthorizeView>`, content in `.page`) and `Components/Layout/AuthLayout.razor` (full-screen centered, used by `Login` via `@layout AuthLayout`).
- **Layout shell:** `MainLayout` wraps the top bar + `<main class="app-main">` in `.app-shell` (a flex column at `height: 100vh`), so `.app-main` fills the viewport below the title bar. Pages that want a centred, max-width look opt into a `.page` wrapper; pages that need full height/bleed use `.app-main` directly.
- **Workspace pages (sidebar + content):** both the home **`BackupServicePage`** (route `/`, title "Backup Service") and the **Settings** page use the shared `.workspace` / `.sidebar` / `.sidebar-item` / `.workspace-content` CSS: a left-docked navigation sidebar (a self-contained control owning its items, two-way `@bind-SelectedKey`) plus a content area that swaps the selected section's control. `BackupServicePage` has `BackupServiceSidebar` (item: Dashboard → `Dashboard.razor`); Settings has `SettingsSidepanel` (item: Authentication → `AuthenticationPanel`). Add a section = one sidebar item + a matching control.
- **Naming caveat:** never name a type or page folder `BackupService` — it equals the root namespace and shadows it (breaks `@using BackupService...`). The home page folder/class is `BackupServicePage` for this reason.
- **Interactivity:** pages are static SSR by default. Components needing interactivity opt in with `@rendermode InteractiveServer` (infrastructure already wired in `Program.cs`). The Settings page is the first such page (the `SettingsSidepanel` nav updates the shown section via `@bind-SelectedKey`). Keep the login page static SSR (it must write the auth cookie on its form POST).
- **Theme/styling:** a single global dark theme in `wwwroot/app.css`, driven by CSS custom properties under `:root` (`--bg`, `--surface`, `--accent`, …) — re-skin by editing those variables. Shared classes: `.topbar`, `.page`, `.card`, `.auth-shell`/`.auth-card`, `.btn-secondary`, `.alert-error`. No CSS framework (no Bootstrap). Note: `wwwroot` is served via the Web SDK static-web-assets system — live from source in Development and copied into `wwwroot` on `publish`; it is **not** copied to `bin/` on a plain `build`, so fetching `/app.css` from a raw `dotnet bin\...dll` run in Production mode 404s (a test artifact only, not a real-deployment issue).
- `Components/Controls/` — shared, reusable custom controls (namespace `BackupService.Components.Controls`, imported globally via `Components/_Imports.razor`). E.g. `Notification` — a non-modal toast: call `Show(message, NotificationLevel)` via `@ref`; it shows bottom-right for 8s then fades (Success/Warning/Error → green/amber/red).
- `Authentication/` — `IAdminCredentialService` / `AdminCredentialService`: seeds the single admin row, verifies login credentials, and changes the password (`ChangePasswordAsync` verifies the current password, then re-hashes), using `PasswordHasher<AdminCredential>` (PBKDF2). Registered as a singleton; uses the DB factory. The settings UI for this lives in `Components/Pages/Authentication/` (`AuthenticationPanel` + self-contained `ChangePasswordDialog` modal).
- `IAuthenticationHistoryService` / `AuthenticationHistoryService` (singleton) — writes an `AuthenticationHistory` audit row (`EventType` + UTC timestamp) for each login success/failure (recorded in `Login.razor.cs`) and password change (recorded in `ChangePasswordDialog`), and reads it back paged (`GetPageAsync`, newest first). `AuthenticationPanel` shows this as a paged table below the Change Password button.
- **Auth model:** cookie authentication for a **single admin account** (username `admin`, default password `admin`, stored **hashed** in the `AdminCredentials` table — a future change will allow editing it). Session uses a **30-minute sliding (inactivity) expiry**. Every page requires authentication via `@attribute [Authorize]` in `Components/Pages/_Imports.razor` (applies to all pages there); the login page opts out with `@attribute [AllowAnonymous]`. Enforcement is by `AuthorizeRouteView` in `Routes.razor`, **not** a global authorization `FallbackPolicy` — a fallback policy is enforced at the Blazor endpoint level and would also block `/login`, causing a redirect loop. `Login.razor` is a **static SSR** page (no interactive render mode) so its form POST is a real HTTP request that can write the auth cookie via the cascaded `HttpContext`.
- Configuration comes from `appsettings.json` plus an environment-specific overlay (`appsettings.Development.json` / `appsettings.Production.json`), selected by `ASPNETCORE_ENVIRONMENT` (set to `Development` in `Properties/launchSettings.json`; unset → Production for the deployed service).
- User secrets are enabled (`UserSecretsId` in the csproj) — use `dotnet user-secrets` for local credentials rather than committing them.

## Conventions

- `Nullable` and `ImplicitUsings` are enabled — common namespaces (e.g. `Microsoft.Extensions.Logging`, `System.Threading`) are available without explicit `using` statements.
- The project uses C# primary constructors (see `Worker(ILogger<Worker> logger)`); follow that style for new injected services.
- **Blazor components use code-behind.** A `.razor` file should hold only markup and the minimal C# unavoidable in markup (e.g. simple `@bind`/event wiring); put the bulk of the logic — fields, properties, lifecycle methods, event handlers, `[Inject]` services — in a sibling `.razor.cs` partial class (`public partial class Foo { ... }` in the component's namespace). Create the code-behind whenever a component needs more than trivial inline code.
- **Each page gets its own folder under `Components/Pages/`.** A routable page lives in `Components/Pages/<PageName>/<PageName>.razor`, with its code-behind and any page-specific files (sub-components, view models, partials) in that same folder. Examples: `Components/Pages/Dashboard/`, `Components/Pages/Settings/`, `Components/Pages/Authentication/` (the Login page). The folder becomes part of the component namespace (e.g. `BackupService.Components.Pages.Authentication`); match the code-behind's `namespace` to it. Routes come from `@page`, not the folder, so moving a page between folders doesn't change its URL. (Note: the page folder `Components/Pages/Authentication/` is distinct from the root `Authentication/` folder, which holds the auth *services*.)
- **For logging, use `Microsoft.Extensions.Logging`** — inject `ILogger<T>` via the constructor (already configured by the host; no third-party logging framework like Serilog or NLog). Use structured-logging message templates (e.g. `logger.LogInformation("Backed up {File}", path)`).
- **Use dependency injection throughout, via the built-in `Microsoft.Extensions.DependencyInjection` container** (available as `builder.Services` — no third-party container like Autofac). Register services on `builder.Services` (`AddSingleton`/`AddScoped`/`AddTransient`) and inject them via constructors rather than newing them up or using static/singleton access. Backup logic should live in injectable services (interface + implementation) consumed by `Worker`, not inline in `Worker` itself.
