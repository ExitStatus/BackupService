using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Logging
{
    [TestFixture]
    public class OperationLogFileMigratorTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private TempLogStore _logStore = null!;
        private OperationLogFileMigrator _migrator = null!;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<BackupDbContext>()
                .UseSqlite(_connection)
                .Options;

            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            var factoryMock = new Mock<IDatabaseContextFactory>();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            _dbFactory = factoryMock.Object;
            _logStore = new TempLogStore();
            _migrator = new OperationLogFileMigrator(_dbFactory, _logStore.Store, NullLogger<OperationLogFileMigrator>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
            _logStore.Dispose();
        }

        [Test]
        public async Task MigrateAsync_CopiesLegacyDetailRowsToFiles_SetsLogFile_AndDropsTable()
        {
            int logId;
            using (var db = new BackupDbContext(_options))
            {
                var log = new OperationLog { Name = "Nightly", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Warning };
                db.OperationLogs.Add(log);
                await db.SaveChangesAsync();
                logId = log.Id;
            }

            // Recreate the legacy OperationLogDetails table (dropped from the current model) and seed rows.
            await ExecuteAsync(
                "CREATE TABLE OperationLogDetails (Id INTEGER PRIMARY KEY AUTOINCREMENT, OperationLogId INTEGER NOT NULL, " +
                "Level INTEGER NOT NULL, Message TEXT NOT NULL, Sequence INTEGER NOT NULL, TimestampUtc TEXT NOT NULL);");
            await SeedDetailAsync(logId, 1, OperationLogLevel.Info, "started");
            await SeedDetailAsync(logId, 2, OperationLogLevel.Warning, "careful");
            await SeedDetailAsync(logId, 3, OperationLogLevel.Info, "done");

            await _migrator.MigrateAsync();

            // Lines are in the file, in sequence order.
            var lines = await _logStore.Store.ReadAsync(logId);
            lines.Select(l => l.Message).Should().Equal("started", "careful", "done");
            lines[1].Level.Should().Be(OperationLogLevel.Warning);

            await using var verify = new BackupDbContext(_options);
            (await verify.OperationLogs.SingleAsync()).LogFile.Should().Be(_logStore.Store.FileNameFor(logId));

            // Table is gone, so a second run is a no-op.
            (await TableExistsAsync()).Should().BeFalse();
            await _migrator.Invoking(m => m.MigrateAsync()).Should().NotThrowAsync();
        }

        [Test]
        public async Task MigrateAsync_WithNoLegacyTable_IsNoOp()
        {
            await _migrator.Invoking(m => m.MigrateAsync()).Should().NotThrowAsync();
        }

        private async Task SeedDetailAsync(int logId, int sequence, OperationLogLevel level, string message)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO OperationLogDetails (OperationLogId, Level, Message, Sequence, TimestampUtc) " +
                "VALUES ($log, $level, $msg, $seq, $ts);";
            command.Parameters.AddWithValue("$log", logId);
            command.Parameters.AddWithValue("$level", (int)level);
            command.Parameters.AddWithValue("$msg", message);
            command.Parameters.AddWithValue("$seq", sequence);
            command.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private async Task<bool> TableExistsAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OperationLogDetails';";
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
    }
}
