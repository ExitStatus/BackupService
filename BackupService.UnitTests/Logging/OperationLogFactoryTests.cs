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
        public async Task CreateAsync_InsertsOperationLogWithLevelAndRecordsItsId()
        {
            var logger = await _factory.CreateAsync("Nightly backup", OperationLogLevel.Warning);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.SingleAsync();

            log.Name.Should().Be("Nightly backup");
            log.Level.Should().Be(OperationLogLevel.Warning);
            log.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            logger.OperationLogId.Should().Be(log.Id);
        }

        [Test]
        public async Task CreateAsync_DefaultsLevelToInfo()
        {
            await _factory.CreateAsync("Op");

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.SingleAsync();

            log.Level.Should().Be(OperationLogLevel.Info);
        }

        [Test]
        public async Task AppendAsync_WritesDetailsWithIncrementingSequence()
        {
            var logger = await _factory.CreateAsync("Op");

            await logger.AppendAsync("started");
            await logger.AppendAsync("more");
            await logger.ErrorAsync("boom");

            await using var context = new BackupDbContext(_options);
            var details = await context.OperationLogDetails.OrderBy(d => d.Sequence).ToListAsync();

            details.Select(d => d.Sequence).Should().Equal(1, 2, 3);
            details.Select(d => d.Message).Should().Equal("started", "more", "boom");
            details.Should().OnlyContain(d => d.OperationLogId == logger.OperationLogId);
        }

        [Test]
        public async Task SetSummaryAsync_UpdatesHeaderMessageAndLevelInPlace()
        {
            var logger = await _factory.CreateAsync("Op started", OperationLogLevel.Info);
            await logger.AppendAsync("did a thing");

            await logger.SetSummaryAsync("Op failed in 5ms", OperationLogLevel.Error);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.SingleAsync();
            log.Name.Should().Be("Op failed in 5ms");
            log.Level.Should().Be(OperationLogLevel.Error);

            // Still a single log header with its detail lines intact.
            (await context.OperationLogDetails.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task AppendAsync_WithMultipleMessages_WritesOneRowPerMessage()
        {
            var logger = await _factory.CreateAsync("Op");

            await logger.AppendAsync("line 1", "line 2", "line 3");

            await using var context = new BackupDbContext(_options);
            var details = await context.OperationLogDetails.OrderBy(d => d.Sequence).ToListAsync();

            details.Select(d => d.Message).Should().Equal("line 1", "line 2", "line 3");
            details.Select(d => d.Sequence).Should().Equal(1, 2, 3);
        }

        [Test]
        public async Task ErrorAsync_WithException_AppendsStackTraceToMessage()
        {
            var logger = await _factory.CreateAsync("Op", OperationLogLevel.Error);

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
            var log = await context.OperationLogs.SingleAsync();
            var detail = await context.OperationLogDetails.SingleAsync();

            log.Level.Should().Be(OperationLogLevel.Error);
            detail.Message.Should().StartWith("failed");
            detail.Message.Should().Contain("InvalidOperationException");
            detail.Message.Should().Contain("kaboom");
            detail.Message.Should().Contain("at "); // a stack-trace frame
        }
    }
}
