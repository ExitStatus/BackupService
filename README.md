# Backup Service

> ⚠️ **Early development — not production ready.** This project is a work in progress and under active
> development. Expect breaking changes, incomplete features, rough edges, and schema migrations between
> updates. **Do not rely on it as your only backup.** Use at your own risk.

A self-hosted backup and folder-sync tool with a local web admin UI. It runs as a single background process
that both serves a Blazor Server dashboard **and** runs the scheduler/watchers that do the actual work —
scheduled and real-time folder syncs, versioned archives, to local disks, network shares, Google Drive, or
USB devices.

## What it does

Backups are organised into **profiles**, each of one type:

| Profile type | What it does |
| --- | --- |
| **One Way Sync** | Scheduled one-way mirror of a source folder into a target (copy/update, optional deletions, overwrite rules). |
| **Two Way Sync** | Scheduled bidirectional sync with a persisted baseline — deletions propagate, and edit/edit conflicts are resolved by a configurable rule (newer wins / source wins / target wins / keep both / skip). |
| **Instant Sync** | Real-time, watcher-driven mirror — changes are copied as they happen, after a short debounce. |
| **Archive Sync** | Scheduled timestamped ZIP archives with retention (keep-last-N or grandfather-father-son), optional AES-256 password protection, and "only archive on change". |
| **Lightroom Archive** | Watcher-driven copy that also pulls the matching RAW originals from a Lightroom catalog into a RAW folder beside each copied file. |

Each profile has per-item source/target folders with **include/exclude filters**, and (for scheduled types) a
friendly schedule builder.

### Other features

- **Connections** to remote targets: **SMB** shares, **Google Drive** (OAuth 2.0), and **USB** devices
  (mass-storage drives and MTP cameras/phones), usable as a profile's source or target. USB profiles can run
  automatically **on device connect** and optionally eject the drive afterwards.
- **Groups** — run a set of profiles sequentially (one at a time) or in parallel.
- **Scheduled Tasks** — cron-scheduled tasks that run ordered OS-command / PowerShell steps.
- **Dashboard** — run history, success rates, data-copied and run-time charts.
- **Desktop integration** (Windows) — system-tray icon, completion/connect notifications, and optional
  on-screen progress windows.
- **Database backup & restore** — scheduled backups of the app's own database to any connection.
- **Logging** — per-run operation logs with a searchable terminal view and configurable retention.
- **Authentication** — a single local admin account (cookie auth) with a login/change-password audit trail.

## Tech stack

- **.NET 10**, ASP.NET Core (`Microsoft.NET.Sdk.Web`) hosting **Blazor Server** plus background hosted services
- **EF Core** over **SQLite** (one per-user database file)
- Charts via **Blazor-ApexCharts**; SMB via **SMBLibrary**; Google Drive via **Google.Apis.Drive.v3**; MTP via
  **MediaDevices**
- Tests: **NUnit** + **Moq** + **FluentAssertions**
- **Cross-platform**: runs on Windows and Linux (the tray icon, notifications, and USB detection are
  Windows-only and no-op elsewhere)

## Running it

Requires the **.NET 10 SDK**. From the repo root:

```bash
dotnet run --project BackupService          # run the web host + workers (Ctrl+C to stop)
dotnet watch --project BackupService run    # with hot reload
dotnet test                                 # run the unit tests
```

The admin UI is **HTTP only** and intended for local use:

- Development: <http://localhost:5080>
- Deployed (Production): <http://localhost:55000>

**Default login:** username `admin`, password `admin` — change it immediately from **Settings →
Authentication** after first sign-in.

### Deploying

Publish with the included profile (deploys to `C:\Tools\BackupService`):

```bash
dotnet publish -p:PublishProfile=Install
```

The deployed app is a plain console executable with three run modes:

- **no args** — runs in the foreground, attached to the terminal
- **`-background`** — relaunches itself detached, logging to `{data dir}\logs\` (Windows only)
- **`-stop`** — signals a running background instance to stop

On Linux, run it in the foreground under **systemd** (a sample unit is in `deploy/backupservice.service`); the
"start on login" option uses an XDG autostart entry. Linux publish profiles (`InstallLinux` / `InstallLinuxUser`)
are also included.

## Data & configuration

- The SQLite database, data-protection keys, logs, and per-item sync state live in a **per-user data
  directory** — `%LOCALAPPDATA%\BackupService` on Windows (deployed) or `~/.local/share/BackupService` on
  Linux. A development run keeps them next to the build output.
- Pending schema migrations are applied automatically on startup.
- Remote secrets (SMB / Google Drive / archive passwords) are **encrypted at rest** via ASP.NET Core Data
  Protection.

## Status & disclaimer

This is a personal project in **early development**. Features may change or be removed, data formats may change
between versions, and it has not been hardened or audited for production use. There is **no warranty** — always
keep an independent copy of anything you care about.

## License

Licensed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE) for the full text.
