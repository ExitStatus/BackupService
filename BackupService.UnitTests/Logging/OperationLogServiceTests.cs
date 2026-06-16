using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Logging
{
    [TestFixture]
    public class OperationLogServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private OperationLogService _service = null!;

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
            _service = new OperationLogService(dbFactory.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task GetPageAsync_ReturnsHeadersNewestFirstAndPages()
        {
            using (var context = new BackupDbContext(_options))
            {
                for (var i = 1; i <= 12; i++)
                {
                    context.OperationLogs.Add(new OperationLog
                    {
                        Name = $"Op {i}",
                        TimestampUtc = DateTimeOffset.UtcNow,
                        Level = OperationLogLevel.Info,
                    });
                }
                await context.SaveChangesAsync();
            }

            var firstPage = await _service.GetPageAsync(1, 10);

            firstPage.TotalCount.Should().Be(12);
            firstPage.TotalPages.Should().Be(2);
            firstPage.Items.Should().HaveCount(10);
            firstPage.Items[0].Name.Should().Be("Op 12"); // newest first
            firstPage.Items[9].Name.Should().Be("Op 3");

            var secondPage = await _service.GetPageAsync(2, 10);

            secondPage.Items.Should().HaveCount(2);
            secondPage.Items[0].Name.Should().Be("Op 2");
            secondPage.Items[1].Name.Should().Be("Op 1");
        }

        [Test]
        public async Task GetDetailsAsync_ReturnsOnlyTargetLogDetailsOrderedBySequence()
        {
            int targetId;
            using (var context = new BackupDbContext(_options))
            {
                var target = new OperationLog { Name = "Target", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info };
                var other = new OperationLog { Name = "Other", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info };
                context.OperationLogs.AddRange(target, other);
                await context.SaveChangesAsync();
                targetId = target.Id;

                context.OperationLogDetails.AddRange(
                    new OperationLogDetail { OperationLogId = target.Id, Message = "third", Sequence = 3, TimestampUtc = DateTimeOffset.UtcNow },
                    new OperationLogDetail { OperationLogId = target.Id, Message = "first", Sequence = 1, TimestampUtc = DateTimeOffset.UtcNow },
                    new OperationLogDetail { OperationLogId = target.Id, Message = "second", Sequence = 2, TimestampUtc = DateTimeOffset.UtcNow },
                    new OperationLogDetail { OperationLogId = other.Id, Message = "other", Sequence = 1, TimestampUtc = DateTimeOffset.UtcNow });
                await context.SaveChangesAsync();
            }

            var details = await _service.GetDetailsAsync(targetId);

            details.Select(d => d.Message).Should().Equal("first", "second", "third");
            details.Should().OnlyContain(d => d.OperationLogId == targetId);
        }

        [Test]
        public async Task GetPageAsync_FilterMatchesNameCaseInsensitively()
        {
            await SeedAsync(
                ("Profile created: Photos", null),
                ("Profile deleted: Documents", null),
                ("Profile updated: Photos archive", null));

            var result = await _service.GetPageAsync(1, 10, filter: "photos");

            result.TotalCount.Should().Be(2);
            result.Items.Select(l => l.Name).Should().BeEquivalentTo(
                "Profile created: Photos", "Profile updated: Photos archive");
        }

        [Test]
        public async Task GetPageAsync_WithoutIncludeMessages_DoesNotMatchOnMessages()
        {
            await SeedAsync(("Profile created: Photos", "Source: C:\\Backups"));

            var result = await _service.GetPageAsync(1, 10, filter: "Backups", includeMessages: false);

            result.TotalCount.Should().Be(0);
        }

        [Test]
        public async Task GetPageAsync_WithIncludeMessages_MatchesOnMessages()
        {
            await SeedAsync(
                ("Profile created: Photos", "Source: C:\\Backups"),
                ("Profile created: Music", "Source: C:\\Media"));

            var result = await _service.GetPageAsync(1, 10, filter: "Backups", includeMessages: true);

            result.TotalCount.Should().Be(1);
            result.Items.Single().Name.Should().Be("Profile created: Photos");
        }

        [Test]
        public async Task GetPageAsync_EmptyFilterReturnsAll()
        {
            await SeedAsync(("One", null), ("Two", null));

            var result = await _service.GetPageAsync(1, 10, filter: "   ", includeMessages: true);

            result.TotalCount.Should().Be(2);
        }

        [Test]
        public async Task GetPageAsync_FiltersByLevel()
        {
            using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.AddRange(
                    new OperationLog { Name = "Info one", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info },
                    new OperationLog { Name = "Error one", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Error },
                    new OperationLog { Name = "Error two", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Error });
                await context.SaveChangesAsync();
            }

            var errors = await _service.GetPageAsync(1, 10, level: OperationLogLevel.Error);

            errors.TotalCount.Should().Be(2);
            errors.Items.Should().OnlyContain(l => l.Level == OperationLogLevel.Error);
        }

        [Test]
        public async Task GetPageAsync_LevelAndNameFilterCombine()
        {
            using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.AddRange(
                    new OperationLog { Name = "Profile created: Photos", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Error },
                    new OperationLog { Name = "Profile created: Music", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Error },
                    new OperationLog { Name = "Profile created: Photos", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info });
                await context.SaveChangesAsync();
            }

            var result = await _service.GetPageAsync(1, 10, filter: "Photos", level: OperationLogLevel.Error);

            result.TotalCount.Should().Be(1);
            result.Items.Single().Level.Should().Be(OperationLogLevel.Error);
        }

        [Test]
        public async Task GetPageAsync_PopulatesDetailCount()
        {
            await SeedAsync(
                ("With detail", "a line"),
                ("Without detail", null));

            var result = await _service.GetPageAsync(1, 10);

            result.Items.Single(l => l.Name == "With detail").DetailCount.Should().Be(1);
            result.Items.Single(l => l.Name == "Without detail").DetailCount.Should().Be(0);
        }

        [Test]
        public async Task GetPageAsync_FiltersByProfileAndIncludesProfileNavigation()
        {
            int photosId;
            using (var context = new BackupDbContext(_options))
            {
                var photos = new Profile { Name = "Photos", DateCreated = DateTimeOffset.UtcNow };
                var music = new Profile { Name = "Music", DateCreated = DateTimeOffset.UtcNow };
                context.Profiles.AddRange(photos, music);
                await context.SaveChangesAsync();
                photosId = photos.Id;

                context.OperationLogs.AddRange(
                    new OperationLog { Name = "Profile created: Photos", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info, ProfileId = photos.Id },
                    new OperationLog { Name = "Profile created: Music", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info, ProfileId = music.Id },
                    new OperationLog { Name = "Unrelated", TimestampUtc = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info, ProfileId = null });
                await context.SaveChangesAsync();
            }

            var result = await _service.GetPageAsync(1, 10, profileId: photosId);

            result.TotalCount.Should().Be(1);
            result.Items.Single().Profile!.Name.Should().Be("Photos");
        }

        private async Task SeedAsync(params (string Name, string? Message)[] logs)
        {
            using var context = new BackupDbContext(_options);
            foreach (var (name, message) in logs)
            {
                var log = new OperationLog
                {
                    Name = name,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Level = OperationLogLevel.Info,
                };
                if (message is not null)
                {
                    log.Details.Add(new OperationLogDetail
                    {
                        Message = message,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        Sequence = 1,
                    });
                }
                context.OperationLogs.Add(log);
            }
            await context.SaveChangesAsync();
        }
    }
}
