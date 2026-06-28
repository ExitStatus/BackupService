using BackupService.Database;
using BackupService.Options;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Options
{
    [TestFixture]
    public class AppOptionsServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private AppOptionsService _service = null!;

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

            _service = new AppOptionsService(_dbFactory);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task GetSettingsAsync_SeedsDefaultsOff_WhenNoneExist()
        {
            var settings = await _service.GetSettingsAsync();

            settings.StartWithWindows.Should().BeFalse();
            settings.ShowTrayIcon.Should().BeFalse();
            settings.AllowNotifications.Should().BeFalse();

            await using var verify = new BackupDbContext(_options);
            (await verify.AppOptions.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task GetSettingsAsync_SeedsOnlyOneRow_WhenCalledTwice()
        {
            await _service.GetSettingsAsync();
            await _service.GetSettingsAsync();

            await using var verify = new BackupDbContext(_options);
            (await verify.AppOptions.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task UpdateSettingsAsync_PersistsFlags_AndRaisesChanged()
        {
            AppOptions? raised = null;
            _service.Changed += s => raised = s;

            await _service.UpdateSettingsAsync(startWithWindows: true, showTrayIcon: true, allowNotifications: false);

            raised.Should().NotBeNull();
            raised!.StartWithWindows.Should().BeTrue();
            raised.ShowTrayIcon.Should().BeTrue();
            raised.AllowNotifications.Should().BeFalse();

            var settings = await _service.GetSettingsAsync();
            settings.StartWithWindows.Should().BeTrue();
            settings.ShowTrayIcon.Should().BeTrue();
            settings.AllowNotifications.Should().BeFalse();

            await using var verify = new BackupDbContext(_options);
            (await verify.AppOptions.CountAsync()).Should().Be(1);
        }
    }
}
