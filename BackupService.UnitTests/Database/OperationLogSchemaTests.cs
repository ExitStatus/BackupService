using BackupService.Database;
using BackupService.Enumerations;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupService.UnitTests.Database
{
    [TestFixture]
    public class OperationLogSchemaTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<BackupDbContext>()
                .UseSqlite(_connection)
                .Options;

            using var context = new BackupDbContext(_options);
            context.Database.EnsureCreated();
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task OperationLog_Header_RoundTrips()
        {
            var now = DateTimeOffset.UtcNow;

            await using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.Add(new OperationLog
                {
                    Name = "Nightly backup",
                    TimestampUtc = now,
                    Level = OperationLogLevel.Warning,
                    LogFile = "42.log", // detail lines live in this file now, not the database
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                var log = await context.OperationLogs.SingleAsync();

                log.Name.Should().Be("Nightly backup");
                log.TimestampUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));
                log.Level.Should().Be(OperationLogLevel.Warning);
                log.LogFile.Should().Be("42.log");
            }
        }

        [Test]
        public async Task DeletingProfile_CascadeDeletesItsOperationLogs_ButKeepsUnrelatedOnes()
        {
            int profileId;
            await using (var context = new BackupDbContext(_options))
            {
                var profile = new Profile { Name = "Photos", DateCreated = DateTimeOffset.UtcNow };
                context.Profiles.Add(profile);
                await context.SaveChangesAsync();
                profileId = profile.Id;

                context.OperationLogs.AddRange(
                    new OperationLog { Name = "Profile created: Photos", TimestampUtc = DateTimeOffset.UtcNow, ProfileId = profileId },
                    new OperationLog { Name = "Profile deleted: Music", TimestampUtc = DateTimeOffset.UtcNow, ProfileId = null });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Remove(await context.Profiles.SingleAsync(p => p.Id == profileId));
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                var remaining = await context.OperationLogs.ToListAsync();
                remaining.Should().ContainSingle();
                remaining[0].Name.Should().Be("Profile deleted: Music");
                remaining[0].ProfileId.Should().BeNull();
            }
        }
    }
}
