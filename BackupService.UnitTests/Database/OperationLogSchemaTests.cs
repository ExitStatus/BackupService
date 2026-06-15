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
        public async Task OperationLog_WithDetails_RoundTrips()
        {
            var now = DateTimeOffset.UtcNow;

            await using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.Add(new OperationLog
                {
                    Name = "Nightly backup",
                    TimestampUtc = now,
                    Details =
                    {
                        new OperationLogDetail
                        {
                            Message = "Started",
                            TimestampUtc = now,
                            Level = OperationLogLevel.Info,
                            Sequence = 1,
                        },
                        new OperationLogDetail
                        {
                            Message = "Disk nearly full",
                            TimestampUtc = now,
                            Level = OperationLogLevel.Warning,
                            Sequence = 2,
                        },
                    },
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                var log = await context.OperationLogs.Include(l => l.Details).SingleAsync();

                log.Name.Should().Be("Nightly backup");
                log.TimestampUtc.Should().BeCloseTo(now, TimeSpan.FromSeconds(1));

                log.Details.Should().HaveCount(2);
                var ordered = log.Details.OrderBy(d => d.Sequence).ToList();
                ordered[0].Message.Should().Be("Started");
                ordered[0].Level.Should().Be(OperationLogLevel.Info);
                ordered[1].Message.Should().Be("Disk nearly full");
                ordered[1].Level.Should().Be(OperationLogLevel.Warning);
            }
        }

        [Test]
        public async Task DeletingOperationLog_CascadeDeletesItsDetails()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.Add(new OperationLog
                {
                    Name = "Op",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Details =
                    {
                        new OperationLogDetail { Message = "a", Level = OperationLogLevel.Debug, Sequence = 1 },
                        new OperationLogDetail { Message = "b", Level = OperationLogLevel.Error, Sequence = 2 },
                    },
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                context.OperationLogs.Remove(await context.OperationLogs.SingleAsync());
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                (await context.OperationLogs.CountAsync()).Should().Be(0);
                (await context.OperationLogDetails.CountAsync()).Should().Be(0);
            }
        }
    }
}
