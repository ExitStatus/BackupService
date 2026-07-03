using System.Diagnostics;
using System.IO.Compression;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Default <see cref="IDatabaseBackupService"/>. The snapshot uses SQLite's <c>VACUUM INTO</c>, which
    /// writes a compact, transactionally-consistent copy even while the live database (WAL included) is in
    /// use — so no writer needs to be quiesced. The zip is built locally and then crash-safe copied into
    /// the target (the dot-<c>.tmp</c>→rename idiom, cross-filesystem like the other engines), so a remote
    /// target (SMB / Google Drive) works the same as a local folder.
    /// </summary>
    public sealed class DatabaseBackupService(
        IDatabaseContextFactory contextFactory,
        IBackupFileSystem fileSystem,
        IEndpointFileSystemFactory endpointFactory,
        IOperationLogFactory operationLogFactory,
        TimeProvider timeProvider,
        ILogger<DatabaseBackupService> logger) : IDatabaseBackupService
    {
        private const string SnapshotFileName = "backupservice.db";

        // Single-instance gate: 1 while a backup is running (a second call is skipped, not queued).
        private int _running;

        public async Task<DatabaseBackupSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();
            return await GetOrSeedAsync(db, cancellationToken);
        }

        public async Task UpdateSettingsAsync(
            bool enabled, int? targetConnectionId, string targetFolder, string? scheduleCron, int maxBackups, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var settings = await GetOrSeedAsync(db, cancellationToken);
            settings.Enabled = enabled;
            settings.TargetConnectionId = targetConnectionId;
            settings.TargetFolder = targetFolder.Trim();
            settings.Schedule = string.IsNullOrWhiteSpace(scheduleCron) ? null : scheduleCron.Trim();
            settings.MaxBackups = Math.Max(1, maxBackups);
            await db.SaveChangesAsync(cancellationToken);

            var targetName = settings.TargetConnectionId is { } id
                ? await db.Connections.Where(c => c.Id == id).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken) ?? $"Connection {id}"
                : "This machine (local)";

            var log = await operationLogFactory.CreateAsync("Database backup settings updated", cancellationToken: cancellationToken);
            await log.AppendAsync(
                $"Automatic backups: {(settings.Enabled ? "Enabled" : "Disabled")}",
                $"Target: {targetName} — '{settings.TargetFolder}'",
                $"Schedule: {ScheduleDefinition.Describe(settings.Schedule)}",
                $"Backups to keep: {settings.MaxBackups}");
        }

        public async Task<bool> RunBackupAsync(bool manual, CancellationToken cancellationToken = default)
        {
            // Only one backup at a time — a second trigger (schedule catching a long manual run, or a
            // double click) is skipped rather than queued.
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                logger.LogInformation("Skipping database backup: one is already in progress.");
                return false;
            }

            try
            {
                var settings = await GetSettingsAsync(cancellationToken);
                if (!manual && !settings.Enabled)
                {
                    return false; // defensive — the scheduler shouldn't fire when disabled
                }

                var prefix = manual ? "[Manual] " : string.Empty;
                var log = await operationLogFactory.CreateAsync($"{prefix}Database backup started", cancellationToken: cancellationToken);
                var stopwatch = Stopwatch.StartNew();
                string? snapshotDir = null, zipDir = null;

                try
                {
                    var fileName = DatabaseBackupNaming.BuildFileName(timeProvider.GetLocalNow().DateTime);

                    // 1. Consistent snapshot of the live database into a unique local temp folder.
                    var snapshotPath = fileSystem.GetTempFilePath(SnapshotFileName);
                    snapshotDir = Path.GetDirectoryName(snapshotPath);
                    await using (var db = contextFactory.CreateDbContext())
                    {
                        // VACUUM INTO takes no parameters; the path is our own temp GUID path, quoted defensively.
                        var escaped = snapshotPath.Replace("'", "''");
                        await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", cancellationToken);
                    }
                    await log.AppendAsync($"Database snapshot created ({ByteSize.Humanize(fileSystem.GetFileSize(snapshotPath))})");

                    // 2. Zip it locally (the snapshot sits alone in its temp folder, so the zip has one entry).
                    var zipPath = fileSystem.GetTempFilePath(fileName);
                    zipDir = Path.GetDirectoryName(zipPath);
                    fileSystem.CreateZipFromDirectory(snapshotDir!, zipPath, includeSubfolders: false);

                    // 3. Crash-safe copy into the target (local folder or connection), then retention.
                    var errors = 0;
                    var pruned = 0;
                    var endpoint = await endpointFactory.ResolveAsync(settings.TargetConnectionId, settings.TargetFolder, cancellationToken);
                    using (endpoint.Session)
                    {
                        var targetDir = endpoint.BasePath;
                        if (!endpoint.FileSystem.DirectoryExists(targetDir))
                        {
                            endpoint.FileSystem.CreateDirectory(targetDir);
                            await log.AppendAsync($"Created folder '{targetDir}'");
                        }

                        var destPath = Path.Combine(targetDir, fileName);
                        await CopyThroughTempAsync(zipPath, destPath, endpoint.FileSystem, cancellationToken);
                        await log.AppendAsync($"Created database backup '{destPath}' ({ByteSize.Humanize(fileSystem.GetFileSize(zipPath))})");

                        // 4. Keep only the newest MaxBackups (foreign files in the folder are never touched).
                        foreach (var expired in DatabaseBackupNaming.SelectExpired(endpoint.FileSystem.GetFiles(targetDir), settings.MaxBackups))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var expiredPath = Path.Combine(targetDir, expired);
                            try
                            {
                                endpoint.FileSystem.DeleteFile(expiredPath);
                                pruned++;
                                await log.AppendAsync($"Pruned backup '{expiredPath}'");
                            }
                            catch (Exception ex)
                            {
                                errors++;
                                await log.ErrorAsync($"Failed to prune backup '{expiredPath}'", ex);
                            }
                        }
                    }

                    var duration = FormatDuration(stopwatch.Elapsed);
                    var outcome = $"'{fileName}' created, {pruned} pruned";
                    await log.SetSummaryAsync(
                        errors == 0
                            ? $"{prefix}Database backup completed in {duration} — {outcome}"
                            : $"{prefix}Database backup completed with {errors} error(s) in {duration} — {outcome}",
                        errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Database backup failed.");
                    await log.ErrorAsync("Database backup failed", ex);
                    await log.SetSummaryAsync($"{prefix}Database backup failed in {FormatDuration(stopwatch.Elapsed)}", OperationLogLevel.Error);
                    return false;
                }
                finally
                {
                    TryDeleteTempDirectory(snapshotDir);
                    TryDeleteTempDirectory(zipDir);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        public async Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(CancellationToken cancellationToken = default)
        {
            var settings = await GetSettingsAsync(cancellationToken);

            var endpoint = await endpointFactory.ResolveAsync(settings.TargetConnectionId, settings.TargetFolder, cancellationToken);
            using (endpoint.Session)
            {
                if (!endpoint.FileSystem.DirectoryExists(endpoint.BasePath))
                {
                    return [];
                }

                var backups = new List<DatabaseBackupInfo>();
                foreach (var path in endpoint.FileSystem.GetFiles(endpoint.BasePath))
                {
                    var name = Path.GetFileName(path);
                    if (!DatabaseBackupNaming.TryParseTimestamp(name, out var timestamp))
                    {
                        continue; // not one of ours
                    }

                    long size = 0;
                    try
                    {
                        size = endpoint.FileSystem.GetFileSize(path);
                    }
                    catch
                    {
                        // Size is display-only — a read failure just shows 0.
                    }

                    backups.Add(new DatabaseBackupInfo(name, timestamp, size));
                }

                return backups.OrderByDescending(b => b.Timestamp).ToList();
            }
        }

        public async Task StageRestoreAsync(string fileName, CancellationToken cancellationToken = default)
        {
            // Only a name we generated is restorable — also blocks any path segments smuggled in.
            if (fileName != Path.GetFileName(fileName) || !DatabaseBackupNaming.TryParseTimestamp(fileName, out _))
            {
                throw new InvalidOperationException("Not a recognised database backup file.");
            }

            var settings = await GetSettingsAsync(cancellationToken);

            var localZip = fileSystem.GetTempFilePath(fileName);
            var tempDir = Path.GetDirectoryName(localZip);
            try
            {
                // Copy the archive locally (streaming, so a remote target works), then extract the
                // database file into the pending-restore slot — applied on the next startup, before
                // anything opens the live database.
                var endpoint = await endpointFactory.ResolveAsync(settings.TargetConnectionId, settings.TargetFolder, cancellationToken);
                using (endpoint.Session)
                {
                    var sourcePath = Path.Combine(endpoint.BasePath, fileName);
                    using var input = endpoint.FileSystem.OpenRead(sourcePath);
                    using var output = fileSystem.OpenWrite(localZip);
                    await input.CopyToAsync(output, cancellationToken);
                }

                using var zip = ZipFile.OpenRead(localZip);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The backup archive contains no database file.");

                entry.ExtractToFile(PendingRestoreApplier.PendingPathFor(BackupDatabaseLocation.GetDataDirectory()), overwrite: true);
            }
            finally
            {
                TryDeleteTempDirectory(tempDir);
            }

            // Self-describing log (message in the name, no lines) — the convention for one-fact events.
            await operationLogFactory.CreateAsync(
                $"Database restore staged from '{fileName}' — restart Backup Service to apply",
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Crash-safe cross-filesystem copy: streams the local file into a dot-prefixed temp on the
        /// target, then renames it over — the same idiom as the other engines. On any failure the temp
        /// is removed (best-effort) so a partial file is never left behind.
        /// </summary>
        private async Task CopyThroughTempAsync(string localSource, string dest, IBackupFileSystem targetFs, CancellationToken cancellationToken)
        {
            var targetDir = Path.GetDirectoryName(dest)!;
            var tempPath = Path.Combine(targetDir, "." + Path.GetFileName(dest) + ".tmp");
            try
            {
                using (var input = fileSystem.OpenRead(localSource))
                using (var output = targetFs.OpenWrite(tempPath))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                if (targetFs.FileExists(dest))
                {
                    targetFs.DeleteFile(dest);
                }
                targetFs.MoveFile(tempPath, dest, overwrite: false);
            }
            catch
            {
                try
                {
                    if (targetFs.FileExists(tempPath))
                    {
                        targetFs.DeleteFile(tempPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup — never mask the original error.
                }
                throw;
            }
        }

        private static async Task<DatabaseBackupSettings> GetOrSeedAsync(BackupDbContext db, CancellationToken cancellationToken)
        {
            var settings = await db.DatabaseBackupSettings.FirstOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = new DatabaseBackupSettings();
                db.DatabaseBackupSettings.Add(settings);
                await db.SaveChangesAsync(cancellationToken);
            }

            return settings;
        }

        private static void TryDeleteTempDirectory(string? directory)
        {
            if (directory is null)
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort — a leftover local temp folder is not fatal.
            }
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
