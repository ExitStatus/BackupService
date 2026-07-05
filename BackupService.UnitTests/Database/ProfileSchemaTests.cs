using BackupService.Database;
using BackupService.Enumerations;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BackupService.UnitTests.Database
{
    [TestFixture]
    public class ProfileSchemaTests
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
        public async Task Profile_WithOneWaySyncItems_RoundTrips()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Add(new Profile
                {
                    Name = "Documents",
                    DateCreated = DateTimeOffset.UtcNow,
                    Schedule = "0 2 * * *",
                    OneWaySyncItems =
                    {
                        new OneWaySyncItem
                        {
                            Name = "Docs pair",
                            SourceFolder = @"C:\Docs",
                            TargetFolder = @"D:\Backup\Docs",
                        },
                    },
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();

                profile.Name.Should().Be("Documents");
                profile.DateLastRun.Should().BeNull();
                profile.Schedule.Should().Be("0 2 * * *");

                var pair = profile.OneWaySyncItems.Should().ContainSingle().Subject;
                pair.Name.Should().Be("Docs pair");
                pair.SourceFolder.Should().Be(@"C:\Docs");
                pair.TargetFolder.Should().Be(@"D:\Backup\Docs");
                pair.Status.Should().Be(OneWaySyncStatus.Idle);
                pair.LastRunStatus.Should().Be(OneWaySyncLastRunStatus.None);
            }
        }

        [Test]
        public async Task DeletingProfile_CascadeDeletesItsOneWaySyncItems()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Add(new Profile
                {
                    Name = "P",
                    DateCreated = DateTimeOffset.UtcNow,
                    OneWaySyncItems =
                    {
                        new OneWaySyncItem { Name = "p1", SourceFolder = "a", TargetFolder = "b" },
                        new OneWaySyncItem { Name = "p2", SourceFolder = "c", TargetFolder = "d" },
                    },
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Remove(await context.Profiles.SingleAsync());
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                (await context.Profiles.CountAsync()).Should().Be(0);
                (await context.OneWaySyncItems.CountAsync()).Should().Be(0);
            }
        }
    }
}
