using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using BackupService.Scheduling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class BackupSchedulerServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private BackupSchedulerService _scheduler = null!;

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

            _scheduler = new BackupSchedulerService(_dbFactory, Mock.Of<IBackupRunner>(), new ProfileStatusService(), NullLogger<BackupSchedulerService>.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            _scheduler.Dispose();
            _connection.Dispose();
        }

        private int SeedProfile(bool enabled, string? schedule)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = "P",
                Type = ProfileType.FolderPair,
                Enabled = enabled,
                Schedule = schedule,
                DateCreated = DateTimeOffset.UtcNow,
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        private void SetProfile(int id, bool enabled, string? schedule)
        {
            using var db = new BackupDbContext(_options);
            var profile = db.Profiles.Single(p => p.Id == id);
            profile.Enabled = enabled;
            profile.Schedule = schedule;
            db.SaveChanges();
        }

        [Test]
        public async Task SyncAsync_SchedulesEnabledProfileWithValidCron()
        {
            var id = SeedProfile(enabled: true, schedule: "*/15 * * * *");

            await _scheduler.SyncAsync(id);

            _scheduler.IsScheduled(id).Should().BeTrue();
        }

        [Test]
        public async Task SyncAsync_UnschedulesWhenDisabled()
        {
            var id = SeedProfile(enabled: true, schedule: "*/15 * * * *");
            await _scheduler.SyncAsync(id);

            SetProfile(id, enabled: false, schedule: "*/15 * * * *");
            await _scheduler.SyncAsync(id);

            _scheduler.IsScheduled(id).Should().BeFalse();
        }

        [Test]
        public async Task SyncAsync_UnschedulesWhenScheduleBlank()
        {
            var id = SeedProfile(enabled: true, schedule: "*/15 * * * *");
            await _scheduler.SyncAsync(id);

            SetProfile(id, enabled: true, schedule: null);
            await _scheduler.SyncAsync(id);

            _scheduler.IsScheduled(id).Should().BeFalse();
        }

        [Test]
        public async Task SyncAsync_DoesNotScheduleInvalidCron()
        {
            var id = SeedProfile(enabled: true, schedule: "not a cron");

            await _scheduler.SyncAsync(id);

            _scheduler.IsScheduled(id).Should().BeFalse();
        }

        [Test]
        public async Task SyncAsync_DeletedProfileIsUnscheduled()
        {
            // A profile id that does not exist (e.g. just deleted) is simply not scheduled.
            await _scheduler.SyncAsync(12345);

            _scheduler.IsScheduled(12345).Should().BeFalse();
        }

        [Test]
        public void GetNextOccurrence_ReturnsNextFutureOccurrence()
        {
            var from = new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);

            var next = BackupSchedulerService.GetNextOccurrence("0 0 * * *", from, TimeZoneInfo.Utc);

            next.Should().Be(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        }

        [TestCase("not a cron")]
        [TestCase("")]
        [TestCase(null)]
        public void GetNextOccurrence_ReturnsNullForUnparseable(string? cron)
        {
            BackupSchedulerService.GetNextOccurrence(cron, DateTimeOffset.UtcNow, TimeZoneInfo.Utc)
                .Should().BeNull();
        }
    }
}
