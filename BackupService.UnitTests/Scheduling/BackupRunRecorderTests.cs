using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Scheduling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class BackupRunRecorderTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;

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
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private async Task<int> SeedProfileAsync()
        {
            await using var db = new BackupDbContext(_options);
            var profile = new Profile { Name = "Docs", Type = ProfileType.FolderPair, DateCreated = DateTimeOffset.UtcNow };
            db.Profiles.Add(profile);
            await db.SaveChangesAsync();
            return profile.Id;
        }

        [Test]
        public async Task RecordAsync_WritesOneRowWithTheRunFields()
        {
            var profileId = await SeedProfileAsync();
            var started = DateTimeOffset.UtcNow;
            var recorder = new BackupRunRecorder(_dbFactory);

            await recorder.RecordAsync(
                profileId,
                ProfileType.FolderPair,
                manual: true,
                startedUtc: started,
                durationMs: 2500.6,
                counts: new BackupResult { Copied = 3, Updated = 1, Deleted = 2, Errors = 4 },
                outcome: RunOutcome.CompletedWithErrors,
                operationLogId: 42);

            await using var verify = new BackupDbContext(_options);
            var run = await verify.BackupRuns.SingleAsync();

            run.ProfileId.Should().Be(profileId);
            run.Type.Should().Be(ProfileType.FolderPair);
            run.Manual.Should().BeTrue();
            run.DurationMs.Should().Be(2501); // rounded from 2500.6
            run.Outcome.Should().Be(RunOutcome.CompletedWithErrors);
            run.Copied.Should().Be(3);
            run.Updated.Should().Be(1);
            run.Deleted.Should().Be(2);
            run.Errors.Should().Be(4);
            run.OperationLogId.Should().Be(42);
        }
    }
}
