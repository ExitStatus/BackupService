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
    public class OperationLogFactoryTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private OperationLogFactory _factory = null!;

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
            _factory = new OperationLogFactory(dbFactory.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task CreateAsync_InsertsOperationLogAndRecordsItsId()
        {
            var logger = await _factory.CreateAsync("Nightly backup");

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.SingleAsync();

            log.Name.Should().Be("Nightly backup");
            log.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            logger.OperationLogId.Should().Be(log.Id);
        }

        [Test]
        public async Task LogMethods_WriteDetailsWithIncrementingSequenceAndLevel()
        {
            var logger = await _factory.CreateAsync("Op");

            await logger.InfoAsync("started");
            await logger.WarningAsync("careful");
            await logger.DebugAsync("details");
            await logger.ErrorAsync("boom");

            await using var context = new BackupDbContext(_options);
            var details = await context.OperationLogDetails.OrderBy(d => d.Sequence).ToListAsync();

            details.Select(d => d.Sequence).Should().Equal(1, 2, 3, 4);
            details.Select(d => d.Level).Should().Equal(
                OperationLogLevel.Info, OperationLogLevel.Warning, OperationLogLevel.Debug, OperationLogLevel.Error);
            details.Select(d => d.Message).Should().Equal("started", "careful", "details", "boom");
            details.Should().OnlyContain(d => d.OperationLogId == logger.OperationLogId);
        }

        [Test]
        public async Task InfoAsync_WithMultipleMessages_WritesOneRowPerMessage()
        {
            var logger = await _factory.CreateAsync("Op");

            await logger.InfoAsync("line 1", "line 2", "line 3");

            await using var context = new BackupDbContext(_options);
            var details = await context.OperationLogDetails.OrderBy(d => d.Sequence).ToListAsync();

            details.Select(d => d.Message).Should().Equal("line 1", "line 2", "line 3");
            details.Select(d => d.Sequence).Should().Equal(1, 2, 3);
            details.Should().OnlyContain(d => d.Level == OperationLogLevel.Info);
        }

        [Test]
        public async Task ErrorAsync_WithException_AppendsStackTraceToMessage()
        {
            var logger = await _factory.CreateAsync("Op");

            Exception caught;
            try
            {
                throw new InvalidOperationException("kaboom");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            await logger.ErrorAsync("failed", caught);

            await using var context = new BackupDbContext(_options);
            var detail = await context.OperationLogDetails.SingleAsync();

            detail.Level.Should().Be(OperationLogLevel.Error);
            detail.Message.Should().StartWith("failed");
            detail.Message.Should().Contain("InvalidOperationException");
            detail.Message.Should().Contain("kaboom");
            detail.Message.Should().Contain("at "); // a stack-trace frame
        }
    }
}
