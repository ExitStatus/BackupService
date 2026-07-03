using BackupService.Database;
using BackupService.DatabaseBackup;
using BackupService.FileSystem;
using BackupService.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.DatabaseBackup
{
    [TestFixture]
    public class DatabaseBackupServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private Mock<IOperationLogFactory> _logFactory = null!;
        private DatabaseBackupService _service = null!;

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

            var dbFactory = new Mock<IDatabaseContextFactory>();
            dbFactory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));

            _logFactory = new Mock<IOperationLogFactory>();
            _logFactory
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<Enumerations.OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Mock.Of<IOperationLogger>());

            _service = new DatabaseBackupService(
                dbFactory.Object,
                Mock.Of<IBackupFileSystem>(),
                Mock.Of<IEndpointFileSystemFactory>(),
                _logFactory.Object,
                TimeProvider.System,
                NullLogger<DatabaseBackupService>.Instance);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task GetSettings_SeedsTheDefaults()
        {
            var settings = await _service.GetSettingsAsync();

            settings.Enabled.Should().BeFalse();
            settings.TargetConnectionId.Should().BeNull();
            settings.TargetFolder.Should().BeEmpty();
            settings.Schedule.Should().BeNull();
            settings.MaxBackups.Should().Be(5);
        }

        [Test]
        public async Task GetSettings_ReturnsTheSameSingleRow()
        {
            var first = await _service.GetSettingsAsync();
            var second = await _service.GetSettingsAsync();

            second.Id.Should().Be(first.Id);
        }

        [Test]
        public async Task UpdateSettings_PersistsTrimmedValues_AndClampsMaxBackups()
        {
            await _service.UpdateSettingsAsync(
                enabled: true,
                targetConnectionId: null,
                targetFolder: @"  C:\Backups\Database  ",
                scheduleCron: "0 3 * * *",
                maxBackups: 0);

            var settings = await _service.GetSettingsAsync();

            settings.Enabled.Should().BeTrue();
            settings.TargetFolder.Should().Be(@"C:\Backups\Database");
            settings.Schedule.Should().Be("0 3 * * *");
            settings.MaxBackups.Should().Be(1); // clamped to at least 1

            // Every settings save is op-logged (the ConnectionService/GroupService convention).
            _logFactory.Verify(
                f => f.CreateAsync(It.Is<string>(n => n.Contains("Database backup settings")), It.IsAny<Enumerations.OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Test]
        public async Task UpdateSettings_BlankScheduleStoresNull()
        {
            await _service.UpdateSettingsAsync(false, null, @"C:\Backups", "   ", 3);

            (await _service.GetSettingsAsync()).Schedule.Should().BeNull();
        }

        [Test]
        public async Task RunBackup_EndToEnd_SnapshotsZipsCopiesAndPrunes()
        {
            // Real filesystem + real VACUUM INTO + real zip against a temp target folder — proves the
            // whole engine end-to-end (the other tests mock the heavy parts).
            var workDir = Path.Combine(Path.GetTempPath(), "BackupServiceTests", Guid.NewGuid().ToString("N"));
            var targetDir = Path.Combine(workDir, "target");
            Directory.CreateDirectory(targetDir);
            try
            {
                var realFs = new BackupFileSystem();
                var endpointFactory = new Mock<IEndpointFileSystemFactory>();
                endpointFactory
                    .Setup(f => f.ResolveAsync(null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new EndpointFileSystem(realFs, targetDir, new NoopSession()));

                var dbFactory = new Mock<IDatabaseContextFactory>();
                dbFactory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));

                var service = new DatabaseBackupService(
                    dbFactory.Object, realFs, endpointFactory.Object, _logFactory.Object,
                    TimeProvider.System, NullLogger<DatabaseBackupService>.Instance);

                // Two old backups + a foreign file already in the target; keep 2 → the oldest backup goes.
                File.WriteAllText(Path.Combine(targetDir, "database-backup_2020-01-01_000000.zip"), "old");
                File.WriteAllText(Path.Combine(targetDir, "database-backup_2020-01-02_000000.zip"), "old");
                File.WriteAllText(Path.Combine(targetDir, "keep-me.txt"), "foreign");

                await service.UpdateSettingsAsync(true, null, targetDir, "0 3 * * *", maxBackups: 2);

                var ran = await service.RunBackupAsync(manual: true);

                ran.Should().BeTrue();
                var zips = Directory.GetFiles(targetDir, "database-backup_*.zip");
                zips.Should().HaveCount(2, "the new backup plus the newest old one survive a keep-2 policy");
                File.Exists(Path.Combine(targetDir, "database-backup_2020-01-01_000000.zip")).Should().BeFalse();
                File.Exists(Path.Combine(targetDir, "keep-me.txt")).Should().BeTrue("foreign files are never pruned");
                Directory.GetFiles(targetDir, "*.tmp").Should().BeEmpty("the crash-safe temp must be renamed away");

                // The newest zip holds the database snapshot.
                var newest = zips.OrderDescending().First();
                using var zip = System.IO.Compression.ZipFile.OpenRead(newest);
                zip.Entries.Should().Contain(e => e.Name == "backupservice.db");
            }
            finally
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private sealed class NoopSession : IDisposable
        {
            public void Dispose()
            {
            }
        }

        [Test]
        public async Task StageRestore_RejectsAFileNameThatIsNotOneOfOurBackups()
        {
            var act = () => _service.StageRestoreAsync("not-a-backup.zip");

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task StageRestore_RejectsAPathSmuggledIntoTheFileName()
        {
            var act = () => _service.StageRestoreAsync(@"..\database-backup_2026-01-01_000000.zip");

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
