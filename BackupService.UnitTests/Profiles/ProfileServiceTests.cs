using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Profiles
{
    [TestFixture]
    public class ProfileServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private ProfileService _service = null!;

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
            _service = new ProfileService(factory.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task CreateAsync_PersistsProfileWithOneFolderPair()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, @"C:\Src", @"D:\Dst", watchFolder: true, scheduleCron: "0 2 * * *");

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.Name.Should().Be("Docs");
            profile.Description.Should().Be("desc");
            profile.Type.Should().Be(ProfileType.FolderPair);
            profile.Schedule.Should().Be("0 2 * * *");
            profile.Status.Should().Be(ProfileStatus.Idle);
            profile.DateCreated.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            profile.DateLastRun.Should().BeNull();

            var pair = profile.FolderPairs.Should().ContainSingle().Subject;
            pair.SourceFolder.Should().Be(@"C:\Src");
            pair.TargetFolder.Should().Be(@"D:\Dst");
            pair.WatchFolder.Should().BeTrue();
            pair.Status.Should().Be(FolderPairStatus.Idle);
            pair.LastRunStatus.Should().Be(FolderPairLastRunStatus.None);
        }

        [Test]
        public async Task GetAsync_ReturnsProfileWithFolderPairs()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, @"C:\Src", @"D:\Dst", watchFolder: false, scheduleCron: null);
            var id = await GetOnlyProfileIdAsync();

            var profile = await _service.GetAsync(id);

            profile.Should().NotBeNull();
            profile!.Name.Should().Be("Docs");
            profile.FolderPairs.Should().ContainSingle().Which.SourceFolder.Should().Be(@"C:\Src");
        }

        [Test]
        public async Task GetAsync_ReturnsNullWhenMissing()
        {
            (await _service.GetAsync(999)).Should().BeNull();
        }

        [Test]
        public async Task UpdateAsync_UpdatesProfileAndFolderPairButNotType()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, @"C:\Src", @"D:\Dst", watchFolder: false, scheduleCron: "0 2 * * *");
            var id = await GetOnlyProfileIdAsync();

            await _service.UpdateAsync(id, "Photos", "new desc", @"C:\Pics", @"E:\Backup", watchFolder: true, scheduleCron: "0 3 * * *");

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.Name.Should().Be("Photos");
            profile.Description.Should().Be("new desc");
            profile.Schedule.Should().Be("0 3 * * *");
            profile.Type.Should().Be(ProfileType.FolderPair);

            var pair = profile.FolderPairs.Should().ContainSingle().Subject;
            pair.SourceFolder.Should().Be(@"C:\Pics");
            pair.TargetFolder.Should().Be(@"E:\Backup");
            pair.WatchFolder.Should().BeTrue();
        }

        [Test]
        public async Task DeleteAsync_RemovesProfileAndFolderPairs()
        {
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, @"C:\Src", @"D:\Dst", watchFolder: false, scheduleCron: null);
            var id = await GetOnlyProfileIdAsync();

            await _service.DeleteAsync(id);

            await using var context = new BackupDbContext(_options);
            (await context.Profiles.CountAsync()).Should().Be(0);
            (await context.FolderPairs.CountAsync()).Should().Be(0);
        }

        [Test]
        public async Task DeleteAsync_IsNoOpWhenMissing()
        {
            var act = async () => await _service.DeleteAsync(999);

            await act.Should().NotThrowAsync();
        }

        private async Task<int> GetOnlyProfileIdAsync()
        {
            await using var context = new BackupDbContext(_options);
            return (await context.Profiles.SingleAsync()).Id;
        }

        [Test]
        public async Task GetPageAsync_SortsByNameAscendingAndDescending()
        {
            await SeedProfilesAsync("Banana", "Apple", "Cherry");

            var ascending = await _service.GetPageAsync(1, 10, ProfileSortColumn.Name, descending: false);
            ascending.Items.Select(p => p.Name).Should().ContainInOrder("Apple", "Banana", "Cherry");

            var descending = await _service.GetPageAsync(1, 10, ProfileSortColumn.Name, descending: true);
            descending.Items.Select(p => p.Name).Should().ContainInOrder("Cherry", "Banana", "Apple");
        }

        [Test]
        public async Task GetPageAsync_SortsByDateLastRunWithNullsHandled()
        {
            await using (var context = new BackupDbContext(_options))
            {
                context.Profiles.AddRange(
                    new Profile { Name = "Old", DateCreated = DateTimeOffset.UtcNow, DateLastRun = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                    new Profile { Name = "Recent", DateCreated = DateTimeOffset.UtcNow, DateLastRun = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                    new Profile { Name = "NeverRun", DateCreated = DateTimeOffset.UtcNow, DateLastRun = null });
                await context.SaveChangesAsync();
            }

            var ascending = await _service.GetPageAsync(1, 10, ProfileSortColumn.DateLastRun, descending: false);
            ascending.Items.Select(p => p.Name).Should().ContainInOrder("NeverRun", "Old", "Recent");

            var descending = await _service.GetPageAsync(1, 10, ProfileSortColumn.DateLastRun, descending: true);
            descending.Items.First().Name.Should().Be("Recent");
        }

        [Test]
        public async Task GetPageAsync_PagesResults()
        {
            await SeedProfilesAsync("A", "B", "C");

            var page = await _service.GetPageAsync(2, 2, ProfileSortColumn.Name, descending: false);

            page.TotalCount.Should().Be(3);
            page.TotalPages.Should().Be(2);
            page.PageNumber.Should().Be(2);
            page.Items.Should().ContainSingle().Which.Name.Should().Be("C");
        }

        private async Task SeedProfilesAsync(params string[] names)
        {
            await using var context = new BackupDbContext(_options);
            foreach (var name in names)
            {
                context.Profiles.Add(new Profile { Name = name, DateCreated = DateTimeOffset.UtcNow });
            }
            await context.SaveChangesAsync();
        }
    }
}
