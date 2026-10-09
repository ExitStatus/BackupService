using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Groups;
using BackupService.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Groups
{
    [TestFixture]
    public class GroupServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private GroupService _service = null!;
        private List<string> _loggedMessages = null!;

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

            _loggedMessages = [];
            var logger = new Mock<IOperationLogger>();
            logger
                .Setup(l => l.AppendAsync(It.IsAny<string[]>()))
                .Callback<string[]>(messages => _loggedMessages.AddRange(messages))
                .Returns(Task.CompletedTask);
            var logFactory = new Mock<IOperationLogFactory>();
            logFactory
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(logger.Object);

            _service = new GroupService(_dbFactory, logFactory.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private int SeedProfile(string name, int? groupId = null, DateTimeOffset? lastRun = null)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = name,
                Type = ProfileType.OneWaySync,
                GroupId = groupId,
                DateLastRun = lastRun,
                DateCreated = DateTimeOffset.UtcNow,
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        [Test]
        public async Task CreateAsync_PersistsGroup_AndReturnsId()
        {
            var id = await _service.CreateAsync("Nightly", GroupConcurrency.Sequential);

            await using var db = new BackupDbContext(_options);
            var group = await db.Groups.SingleAsync();

            group.Id.Should().Be(id);
            group.Name.Should().Be("Nightly");
            group.Concurrency.Should().Be(GroupConcurrency.Sequential);
        }

        [Test]
        public async Task UpdateAsync_ChangesNameAndConcurrency()
        {
            var id = await _service.CreateAsync("Nightly", GroupConcurrency.Sequential);

            await _service.UpdateAsync(id, "Weekly", GroupConcurrency.Parallel);

            await using var db = new BackupDbContext(_options);
            var group = await db.Groups.SingleAsync();
            group.Name.Should().Be("Weekly");
            group.Concurrency.Should().Be(GroupConcurrency.Parallel);
        }

        [Test]
        public async Task GetSummariesAsync_ReturnsNameAscending()
        {
            await _service.CreateAsync("Zebra", GroupConcurrency.Parallel);
            await _service.CreateAsync("Alpha", GroupConcurrency.Sequential);

            var summaries = await _service.GetSummariesAsync();

            summaries.Select(s => s.Name).Should().ContainInOrder("Alpha", "Zebra");
        }

        [Test]
        public async Task GetProfileStatsAsync_CountsMembers_MaxLastRun_AndProfileIds()
        {
            var id = await _service.CreateAsync("Nightly", GroupConcurrency.Sequential);
            var newer = new DateTimeOffset(2026, 7, 1, 3, 0, 0, TimeSpan.Zero);
            var older = new DateTimeOffset(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);
            var p1 = SeedProfile("A", groupId: id, lastRun: older);
            var p2 = SeedProfile("B", groupId: id, lastRun: newer);
            SeedProfile("Ungrouped"); // not in any group — must be excluded

            var stats = await _service.GetProfileStatsAsync();

            stats.Should().ContainKey(id);
            stats[id].Count.Should().Be(2);
            stats[id].LastRun.Should().Be(newer);
            stats[id].ProfileIds.Should().BeEquivalentTo(new[] { p1, p2 });
        }

        [Test]
        public async Task GetProfileStatsAsync_OmitsGroupsWithNoMembers()
        {
            var id = await _service.CreateAsync("Empty", GroupConcurrency.Sequential);

            var stats = await _service.GetProfileStatsAsync();

            stats.Should().NotContainKey(id);
        }

        [Test]
        public async Task DeleteAsync_UngroupsMemberProfiles_AndReturnsCount()
        {
            var id = await _service.CreateAsync("Nightly", GroupConcurrency.Sequential);
            var p1 = SeedProfile("A", groupId: id);
            var p2 = SeedProfile("B", groupId: id);

            var ungrouped = await _service.DeleteAsync(id);

            ungrouped.Should().Be(2);

            await using var db = new BackupDbContext(_options);
            (await db.Groups.AnyAsync()).Should().BeFalse();
            (await db.Profiles.Where(p => p.Id == p1 || p.Id == p2).AllAsync(p => p.GroupId == null)).Should().BeTrue();
        }

        [Test]
        public async Task DeleteAsync_MissingGroup_ReturnsZero()
        {
            var ungrouped = await _service.DeleteAsync(999);

            ungrouped.Should().Be(0);
        }
    }
}
