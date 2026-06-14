using BackupService.Authentication;
using BackupService.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Authentication
{
    [TestFixture]
    public class AuthenticationHistoryServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private AuthenticationHistoryService _service = null!;

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

            var factory = new Mock<IDatabaseContextFactory>();
            factory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));

            _service = new AuthenticationHistoryService(factory.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task RecordAsync_AddsRow_WithEventTypeAndUtcTimestamp()
        {
            var before = DateTimeOffset.UtcNow;

            await _service.RecordAsync(AuthenticationEventType.LoginFailed);

            await using var context = new BackupDbContext(_options);
            var entry = await context.AuthenticationHistory.SingleAsync();

            entry.EventType.Should().Be(AuthenticationEventType.LoginFailed);
            entry.TimestampUtc.Should().BeOnOrAfter(before);
            entry.TimestampUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
        }

        [Test]
        public async Task GetPageAsync_OrdersNewestFirst_AndReturnsRequestedPageWithTotalCount()
        {
            // 12 events recorded oldest-to-newest.
            for (var i = 0; i < 12; i++)
            {
                await _service.RecordAsync(AuthenticationEventType.LoginSucceeded);
            }

            var page1 = await _service.GetPageAsync(pageNumber: 1, pageSize: 10);
            page1.TotalCount.Should().Be(12);
            page1.TotalPages.Should().Be(2);
            page1.Items.Should().HaveCount(10);

            var page2 = await _service.GetPageAsync(pageNumber: 2, pageSize: 10);
            page2.Items.Should().HaveCount(2);

            // Newest first: the first item overall has the largest Id (most recently inserted).
            var newestId = page1.Items.First().Id;
            var allIds = page1.Items.Concat(page2.Items).Select(e => e.Id).ToList();
            newestId.Should().Be(allIds.Max());
            allIds.Should().BeInDescendingOrder();
        }

        [Test]
        public async Task GetPageAsync_WhenEmpty_ReturnsZeroTotalAndNoItems()
        {
            var page = await _service.GetPageAsync(pageNumber: 1, pageSize: 10);

            page.TotalCount.Should().Be(0);
            page.TotalPages.Should().Be(0);
            page.Items.Should().BeEmpty();
        }
    }
}
