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
        public async Task Profile_WithFolderPairs_RoundTrips()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Add(new Profile
                {
                    Name = "Documents",
                    Description = "Back up my documents",
                    DateCreated = DateTimeOffset.UtcNow,
                    Status = ProfileStatus.Idle,
                    Schedule = "0 2 * * *",
                    FolderPairs =
                    {
                        new FolderPair
                        {
                            Name = "Docs pair",
                            SourceFolder = @"C:\Docs",
                            TargetFolder = @"D:\Backup\Docs",
                            WatchFolder = true,
                        },
                    },
                });
                await context.SaveChangesAsync();
            }

            await using (var context = new BackupDbContext(_options))
            {
                var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

                profile.Name.Should().Be("Documents");
                profile.Description.Should().Be("Back up my documents");
                profile.Status.Should().Be(ProfileStatus.Idle);
                profile.DateLastRun.Should().BeNull();
                profile.Schedule.Should().Be("0 2 * * *");

                var pair = profile.FolderPairs.Should().ContainSingle().Subject;
                pair.Name.Should().Be("Docs pair");
                pair.SourceFolder.Should().Be(@"C:\Docs");
                pair.TargetFolder.Should().Be(@"D:\Backup\Docs");
                pair.WatchFolder.Should().BeTrue();
                pair.Status.Should().Be(FolderPairStatus.Idle);
                pair.LastRunStatus.Should().Be(FolderPairLastRunStatus.None);
            }
        }

        [Test]
        public async Task DeletingProfile_CascadeDeletesItsFolderPairs()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.Add(new Profile
                {
                    Name = "P",
                    DateCreated = DateTimeOffset.UtcNow,
                    FolderPairs =
                    {
                        new FolderPair { Name = "p1", SourceFolder = "a", TargetFolder = "b" },
                        new FolderPair { Name = "p2", SourceFolder = "c", TargetFolder = "d" },
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
                (await context.FolderPairs.CountAsync()).Should().Be(0);
            }
        }
    }
}
