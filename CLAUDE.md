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

Running interactively (console / `dotnet run`) starts Kestrel and serves the Blazor UI. `UseWindowsService()` only changes the host lifetime when the process is actually started by the Windows SCM, so local console runs are unaffected. To install as a Windows Service, publish then register with `sc.exe create` pointing at the published `.exe`.

**HTTP only — bound to `http://localhost:5080`.** There is no HTTPS (the admin UI is local-only). The deployed-service binding comes from the `Kestrel:Endpoints:Http:Url` key in `appsettings.json` (the `urls` host key is *not* read from appsettings, only from env/CLI). The dev launch profile sets the same URL via `applicationUrl` in `launchSettings.json` (which is dev-only and not deployed). The ASP.NET Core dev HTTPS certificate / "trust the self-signed cert" prompt is irrelevant here and does not apply to the service. If the UI ever needs network/HTTPS access, configure a real cert under `Kestrel:Endpoints` and add `CookieSecurePolicy.Always` + `UseHttpsRedirection()`.

Note when running the built dll directly for testing: set the working directory to the output folder, otherwise the content root is the current directory and `appsettings.json` (hence the Kestrel binding) won't be found. The real service is unaffected because `UseWindowsService()` sets the content root to the exe's directory.

There is no test project yet. When adding one, create it as a sibling under the solution (e.g. `BackupService.Tests/`) and add it to `BackupService.slnx`. Run a single test with:

```powershell
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

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
  - **Single database, location depends on hosting** (`BackupDatabaseLocation.GetConnectionString()`): there is only ever one SQLite file. When running as a Windows Service it lives under ProgramData (`%ProgramData%\BackupService\backupservice.db`) so it's shared machine-wide regardless of the service account; when run interactively (debugging/console) it sits next to the exe (`AppContext.BaseDirectory`). The decision uses `WindowsServiceHelpers.IsWindowsService()`. There is no connection string in `appsettings.json` — it is computed in code.
- `Components/` — the Blazor Server UI. `App.razor` is the root HTML document (loads `_framework/blazor.web.js`), `Routes.razor` wires the router via `AuthorizeRouteView` (unauthenticated → `RedirectToLogin`), `Components/Layout/MainLayout.razor` is the default layout (shows the signed-in user + a Log out form, gated by `<AuthorizeView>`), and routable pages live in `Components/Pages/` (`@page` directives). `_Imports.razor` holds shared `@using`s for the component tree.
- `Authentication/` — `IAdminCredentialService` / `AdminCredentialService`: seeds the single admin row and verifies login credentials, hashing with `PasswordHasher<AdminCredential>` (PBKDF2). Registered as a singleton; uses the DB factory.
- **Auth model:** cookie authentication for a **single admin account** (username `admin`, default password `admin`, stored **hashed** in the `AdminCredentials` table — a future change will allow editing it). Session uses a **30-minute sliding (inactivity) expiry**. Every page requires authentication via `@attribute [Authorize]` in `Components/Pages/_Imports.razor` (applies to all pages there); the login page opts out with `@attribute [AllowAnonymous]`. Enforcement is by `AuthorizeRouteView` in `Routes.razor`, **not** a global authorization `FallbackPolicy` — a fallback policy is enforced at the Blazor endpoint level and would also block `/login`, causing a redirect loop. `Login.razor` is a **static SSR** page (no interactive render mode) so its form POST is a real HTTP request that can write the auth cookie via the cascaded `HttpContext`.
- Configuration comes from `appsettings.json` / `appsettings.Development.json`, selected by the `DOTNET_ENVIRONMENT` variable (set to `Development` in `Properties/launchSettings.json`).
- User secrets are enabled (`UserSecretsId` in the csproj) — use `dotnet user-secrets` for local credentials rather than committing them.

## Conventions

- `Nullable` and `ImplicitUsings` are enabled — common namespaces (e.g. `Microsoft.Extensions.Logging`, `System.Threading`) are available without explicit `using` statements.
- The project uses C# primary constructors (see `Worker(ILogger<Worker> logger)`); follow that style for new injected services.
- **Blazor components use code-behind.** A `.razor` file should hold only markup and the minimal C# unavoidable in markup (e.g. simple `@bind`/event wiring); put the bulk of the logic — fields, properties, lifecycle methods, event handlers, `[Inject]` services — in a sibling `.razor.cs` partial class (`public partial class Foo { ... }` in the component's namespace). Create the code-behind whenever a component needs more than trivial inline code.
- **For logging, use `Microsoft.Extensions.Logging`** — inject `ILogger<T>` via the constructor (already configured by the host; no third-party logging framework like Serilog or NLog). Use structured-logging message templates (e.g. `logger.LogInformation("Backed up {File}", path)`).
- **Use dependency injection throughout, via the built-in `Microsoft.Extensions.DependencyInjection` container** (available as `builder.Services` — no third-party container like Autofac). Register services on `builder.Services` (`AddSingleton`/`AddScoped`/`AddTransient`) and inject them via constructors rather than newing them up or using static/singleton access. Backup logic should live in injectable services (interface + implementation) consumed by `Worker`, not inline in `Worker` itself.
