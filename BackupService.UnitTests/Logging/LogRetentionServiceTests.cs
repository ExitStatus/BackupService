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
    public class LogRetentionServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private FakeTimeProvider _time = null!;
        private LogRetentionService _service = null!;
        private TempLogStore _logStore = null!;

        // A fixed "now" for deterministic cutoffs.
        private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

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

            _time = new FakeTimeProvider { UtcNow = Now };
            _logStore = new TempLogStore();
            _service = new LogRetentionService(_dbFactory, _logStore.Store, _time, NullLogger<LogRetentionService>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
            _logStore.Dispose();
        }

        [Test]
        public async Task GetSettingsAsync_SeedsDefaults_WhenNoneExist()
        {
            var settings = await _service.GetSettingsAsync();

            settings.AuthenticationLogRetentionDays.Should().Be(7);
            settings.OperationLogRetentionDays.Should().Be(30);

            await using var verify = new BackupDbContext(_options);
            (await verify.LogRetentionSettings.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task UpdateSettingsAsync_PersistsValues_AndClampsBelowOne()
        {
            await _service.UpdateSettingsAsync(10, 60);

            var settings = await _service.GetSettingsAsync();
            settings.AuthenticationLogRetentionDays.Should().Be(10);
            settings.OperationLogRetentionDays.Should().Be(60);

            await _service.UpdateSettingsAsync(0, -5);

            settings = await _service.GetSettingsAsync();
            settings.AuthenticationLogRetentionDays.Should().Be(1);
            settings.OperationLogRetentionDays.Should().Be(1);

            await using var verify = new BackupDbContext(_options);
            (await verify.LogRetentionSettings.CountAsync()).Should().Be(1); // still a single row
        }

        [Test]
        public async Task UpdateSettingsAsync_ClampsAVeryLargeValue_ToTheMaximum()
        {
            await _service.UpdateSettingsAsync(999_999, int.MaxValue);

            var settings = await _service.GetSettingsAsync();
            settings.AuthenticationLogRetentionDays.Should().Be(LogRetentionService.MaxRetentionDays);
            settings.OperationLogRetentionDays.Should().Be(LogRetentionService.MaxRetentionDays);
        }

        [Test]
        public async Task PurgeIfDueAsync_AVeryLargeSavedRetention_DoesNotStopTheOtherPurge()
        {
            // 999,999 days puts the cutoff before year 1, which threw — and the operation-log purge (run after the
            // authentication one) never happened again.
            using (var db = new BackupDbContext(_options))
            {
                db.LogRetentionSettings.Add(new LogRetentionSettings { AuthenticationLogRetentionDays = 999_999, OperationLogRetentionDays = 30 });
                db.OperationLogs.Add(new OperationLog { Name = "old", TimestampUtc = Now.AddDays(-40) });
                db.OperationLogs.Add(new OperationLog { Name = "recent", TimestampUtc = Now.AddDays(-5) });
                db.SaveChanges();
            }

            await _service.PurgeIfDueAsync();

            await using var verify = new BackupDbContext(_options);
            (await verify.OperationLogs.ToListAsync()).Should().ContainSingle().Which.Name.Should().Be("recent");
        }

        [Test]
        public async Task PurgeIfDueAsync_DeletesRowsOlderThanRetention_KeepsNewer()
        {
            // Defaults: auth 7 days, operation 30 days. Insert oldest first so Id order matches time.
            int oldId, recentId;
            using (var db = new BackupDbContext(_options))
            {
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginFailed, TimestampUtc = Now.AddDays(-10) }); // old
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginSucceeded, TimestampUtc = Now.AddDays(-1) }); // recent

                var old = new OperationLog { Name = "old", TimestampUtc = Now.AddDays(-40) };
                var recent = new OperationLog { Name = "recent", TimestampUtc = Now.AddDays(-5) };
                db.OperationLogs.AddRange(old, recent);
                db.SaveChanges();
                oldId = old.Id;
                recentId = recent.Id;

                old.LogFile = _logStore.Store.FileNameFor(oldId);
                recent.LogFile = _logStore.Store.FileNameFor(recentId);
                db.SaveChanges();
            }

            await _logStore.Store.AppendAsync(oldId, OperationLogLevel.Info, ["old line"]);
            await _logStore.Store.AppendAsync(recentId, OperationLogLevel.Info, ["recent line"]);

            await _service.PurgeIfDueAsync();

            await using var verify = new BackupDbContext(_options);
            var auth = await verify.AuthenticationHistory.ToListAsync();
            auth.Should().ContainSingle().Which.EventType.Should().Be(AuthenticationEventType.LoginSucceeded);

            var logs = await verify.OperationLogs.ToListAsync();
            logs.Should().ContainSingle().Which.Name.Should().Be("recent");

            // The purged log's file is gone; the surviving log's file remains.
            (await _logStore.Store.ReadAsync(oldId)).Should().BeEmpty();
            (await _logStore.Store.ReadAsync(recentId)).Should().ContainSingle()
                .Which.Message.Should().Be("recent line");
        }

        [Test]
        public async Task ClearOperationLogsAsync_DeletesAllOperationLogs_Details_AndRunHistory_LeavesAuthHistory()
        {
            int withLinesId;
            using (var db = new BackupDbContext(_options))
            {
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginSucceeded, TimestampUtc = Now });
                var a = new OperationLog { Name = "a", TimestampUtc = Now.AddDays(-1) };
                db.OperationLogs.Add(a);
                db.OperationLogs.Add(new OperationLog { Name = "b", TimestampUtc = Now }); // detail-less
                var profile = new Profile { Name = "p", Type = ProfileType.OneWaySync };
                db.Profiles.Add(profile);
                db.SaveChanges();
                withLinesId = a.Id;
                a.LogFile = _logStore.Store.FileNameFor(withLinesId);
                db.BackupRuns.Add(new BackupRun { ProfileId = profile.Id, Type = ProfileType.OneWaySync, StartedUtc = Now, Outcome = RunOutcome.Success });
                db.SaveChanges();
            }

            await _logStore.Store.AppendAsync(withLinesId, OperationLogLevel.Info, ["line"]);

            var removed = await _service.ClearOperationLogsAsync();

            removed.Should().Be(2);

            await using var verify = new BackupDbContext(_options);
            (await verify.OperationLogs.CountAsync()).Should().Be(0);
            (await _logStore.Store.ReadAsync(withLinesId)).Should().BeEmpty(); // log files cleared
            (await verify.BackupRuns.CountAsync()).Should().Be(0); // dashboard stats cleared
            (await verify.AuthenticationHistory.CountAsync()).Should().Be(1); // unaffected
        }

        [Test]
        public async Task PurgeIfDueAsync_RunsOncePerDay_ThenAgainNextDay()
        {
            await SeedOldAuthAsync();

            // First call (day D) purges.
            await _service.PurgeIfDueAsync();
            (await AuthCountAsync()).Should().Be(0);

            // A new old row arrives the same day — the guard means a repeat call is a no-op.
            await SeedOldAuthAsync();
            await _service.PurgeIfDueAsync();
            (await AuthCountAsync()).Should().Be(1);

            // Next day, the purge runs again.
            _time.UtcNow = Now.AddDays(1);
            await _service.PurgeIfDueAsync();
            (await AuthCountAsync()).Should().Be(0);
        }

        private async Task SeedOldAuthAsync()
        {
            await using var db = new BackupDbContext(_options);
            db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginFailed, TimestampUtc = Now.AddDays(-10) });
            await db.SaveChangesAsync();
        }

        private async Task<int> AuthCountAsync()
        {
            await using var db = new BackupDbContext(_options);
            return await db.AuthenticationHistory.CountAsync();
        }

        private sealed class FakeTimeProvider : TimeProvider
        {
            public DateTimeOffset UtcNow { get; set; }

            public override DateTimeOffset GetUtcNow() => UtcNow;
        }
    }
}
