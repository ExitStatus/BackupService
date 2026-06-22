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
            _service = new LogRetentionService(_dbFactory, _time, NullLogger<LogRetentionService>.Instance);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

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
        public async Task PurgeIfDueAsync_DeletesRowsOlderThanRetention_KeepsNewer()
        {
            // Defaults: auth 7 days, operation 30 days. Insert oldest first so Id order matches time.
            using (var db = new BackupDbContext(_options))
            {
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginFailed, TimestampUtc = Now.AddDays(-10) }); // old
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginSucceeded, TimestampUtc = Now.AddDays(-1) }); // recent

                db.OperationLogs.Add(new OperationLog
                {
                    Name = "old", TimestampUtc = Now.AddDays(-40),
                    Details = { new OperationLogDetail { Message = "old line", TimestampUtc = Now.AddDays(-40), Sequence = 1 } },
                });
                db.OperationLogs.Add(new OperationLog
                {
                    Name = "recent", TimestampUtc = Now.AddDays(-5),
                    Details = { new OperationLogDetail { Message = "recent line", TimestampUtc = Now.AddDays(-5), Sequence = 1 } },
                });
                db.SaveChanges();
            }

            await _service.PurgeIfDueAsync();

            await using var verify = new BackupDbContext(_options);
            var auth = await verify.AuthenticationHistory.ToListAsync();
            auth.Should().ContainSingle().Which.EventType.Should().Be(AuthenticationEventType.LoginSucceeded);

            var logs = await verify.OperationLogs.ToListAsync();
            logs.Should().ContainSingle().Which.Name.Should().Be("recent");

            var details = await verify.OperationLogDetails.ToListAsync();
            details.Should().ContainSingle().Which.Message.Should().Be("recent line");
        }

        [Test]
        public async Task ClearOperationLogsAsync_DeletesAllOperationLogs_Details_AndRunHistory_LeavesAuthHistory()
        {
            using (var db = new BackupDbContext(_options))
            {
                db.AuthenticationHistory.Add(new AuthenticationHistory { EventType = AuthenticationEventType.LoginSucceeded, TimestampUtc = Now });
                db.OperationLogs.Add(new OperationLog
                {
                    Name = "a", TimestampUtc = Now.AddDays(-1),
                    Details = { new OperationLogDetail { Message = "line", TimestampUtc = Now.AddDays(-1), Sequence = 1 } },
                });
                db.OperationLogs.Add(new OperationLog { Name = "b", TimestampUtc = Now }); // detail-less
                var profile = new Profile { Name = "p", Type = ProfileType.FolderPair };
                db.Profiles.Add(profile);
                db.SaveChanges();
                db.BackupRuns.Add(new BackupRun { ProfileId = profile.Id, Type = ProfileType.FolderPair, StartedUtc = Now, Outcome = RunOutcome.Success });
                db.SaveChanges();
            }

            var removed = await _service.ClearOperationLogsAsync();

            removed.Should().Be(2);

            await using var verify = new BackupDbContext(_options);
            (await verify.OperationLogs.CountAsync()).Should().Be(0);
            (await verify.OperationLogDetails.CountAsync()).Should().Be(0);
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
