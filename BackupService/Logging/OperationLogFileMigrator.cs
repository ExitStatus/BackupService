using System.Globalization;
using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Logging
{
    /// <summary>
    /// One-time migration from the old <c>OperationLogDetails</c> table (one database row per log line) to the
    /// per-log files used by <see cref="IOperationLogFileStore"/>. Runs at startup after the EF migrations:
    /// if the legacy table still exists, it copies each log's detail lines into a file (preserving each line's
    /// level, timestamp and any embedded newlines), records the <see cref="OperationLog.LogFile"/> reference,
    /// then drops the table. Idempotent — once the table is gone it does nothing.
    ///
    /// <para>The table is dropped here (not via an EF migration) because the copy is file I/O that a SQL
    /// migration can't do, and the drop must happen only after the copy has succeeded — a single
    /// <c>Database.Migrate()</c> pass would otherwise drop the table before this code could read it.</para>
    /// </summary>
    public sealed class OperationLogFileMigrator(
        IDatabaseContextFactory contextFactory,
        IOperationLogFileStore fileStore,
        ILogger<OperationLogFileMigrator> logger)
    {
        public async Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            if (!await TableExistsAsync(db, cancellationToken))
            {
                return;
            }

            var migrated = 0;
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                // Read every detail line ordered by (log, sequence) so a single forward pass groups them.
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT OperationLogId, Message, Level, TimestampUtc " +
                    "FROM OperationLogDetails ORDER BY OperationLogId, Sequence";

                var currentLogId = -1;
                var lines = new List<OperationLogLine>();

                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var logId = reader.GetInt32(0);
                        var message = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        var level = (OperationLogLevel)reader.GetInt32(2);
                        var timestamp = ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3));

                        if (logId != currentLogId)
                        {
                            if (currentLogId != -1 && lines.Count > 0)
                            {
                                await WriteAndFlagAsync(db, currentLogId, lines, cancellationToken);
                                migrated++;
                            }
                            currentLogId = logId;
                            lines = [];
                        }

                        lines.Add(new OperationLogLine(level, timestamp, message));
                    }
                }

                if (currentLogId != -1 && lines.Count > 0)
                {
                    await WriteAndFlagAsync(db, currentLogId, lines, cancellationToken);
                    migrated++;
                }

                // The data is safely in files — drop the legacy table.
                await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"OperationLogDetails\";", cancellationToken);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            logger.LogInformation("Migrated detail lines for {Count} operation log(s) to files and dropped the OperationLogDetails table.", migrated);
        }

        private async Task WriteAndFlagAsync(BackupDbContext db, int logId, IReadOnlyList<OperationLogLine> lines, CancellationToken cancellationToken)
        {
            await fileStore.WriteAllAsync(logId, lines, cancellationToken);

            // Record the file reference on the header so the grid shows an expand control for it.
            await db.OperationLogs
                .Where(l => l.Id == logId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LogFile, fileStore.FileNameFor(logId)), cancellationToken);
        }

        private static async Task<bool> TableExistsAsync(BackupDbContext db, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OperationLogDetails';";
                var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                return count > 0;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        // EF stores DateTimeOffset as ISO text (e.g. "2026-07-01 08:30:00.1234567+00:00"). Convert to local
        // wall-clock for the file (matching how new lines are written and displayed).
        private static DateTimeOffset ParseTimestamp(string? value)
        {
            if (!string.IsNullOrEmpty(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToLocalTime();
            }

            return DateTimeOffset.Now;
        }
    }
}
