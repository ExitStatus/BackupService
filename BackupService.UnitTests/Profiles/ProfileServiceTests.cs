using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
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
            _service = new ProfileService(factory.Object, new OperationLogFactory(factory.Object));
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task CreateAsync_PersistsProfileWithOneFolderPair()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, "0 2 * * *", enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: true, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.AlwaysOverwrite)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.Name.Should().Be("Docs");
            profile.Description.Should().Be("desc");
            profile.Type.Should().Be(ProfileType.FolderPair);
            profile.Schedule.Should().Be("0 2 * * *");
            profile.Enabled.Should().BeTrue();
            profile.Status.Should().Be(ProfileStatus.Idle);
            profile.DateCreated.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            profile.DateLastRun.Should().BeNull();

            var pair = profile.FolderPairs.Should().ContainSingle().Subject;
            pair.SourceFolder.Should().Be(@"C:\Src");
            pair.TargetFolder.Should().Be(@"D:\Dst");
            pair.WatchFolder.Should().BeTrue();
            pair.AllowDeletions.Should().BeTrue();
            pair.OverwriteBehaviour.Should().Be(OverwriteBehaviour.AlwaysOverwrite);
            pair.Status.Should().Be(FolderPairStatus.Idle);
            pair.LastRunStatus.Should().Be(FolderPairLastRunStatus.None);
        }

        [Test]
        public async Task CreateAsync_PersistsMultipleFolderPairs()
        {
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, null, enabled: true,
            [
                new FolderPairInput(0, "A", @"C:\A", @"D:\A", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new FolderPairInput(0, "B", @"C:\B", @"D:\B", WatchFolder: true, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.FolderPairs.Select(p => p.SourceFolder).Should().BeEquivalentTo([@"C:\A", @"C:\B"]);
        }

        [Test]
        public async Task CreateAsync_WritesOperationLogWithProfileDetails()
        {
            await _service.CreateAsync("Docs", "my docs", ProfileType.FolderPair, "0 2 * * *", enabled: true,
            [
                new FolderPairInput(0, "Pair A", @"C:\A", @"D:\A", WatchFolder: true, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.AlwaysOverwrite),
                new FolderPairInput(0, "Pair B", @"C:\B", @"D:\B", WatchFolder: false, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.Include(l => l.Details).SingleAsync();

            log.Name.Should().Be("Profile created: Docs");

            var messages = log.Details.OrderBy(d => d.Sequence).Select(d => d.Message).ToList();
            messages.Should().Contain("Name: Docs");
            messages.Should().Contain("Description: my docs");
            messages.Should().Contain(m => m.StartsWith("Type:"));
            messages.Should().Contain(m => m.StartsWith("Schedule:"));
            messages.Should().Contain("Enabled: Yes");
            // Each folder-pair detail is its own row, not one delimited string.
            messages.Should().Contain("Folder pair: Pair A");
            messages.Should().Contain(@"Source: C:\A");
            messages.Should().Contain(@"Target: D:\A");
            messages.Should().Contain("Folder pair: Pair B");
            messages.Should().Contain("Allow deletions: Yes");
        }

        [Test]
        public async Task GetAsync_ReturnsProfileWithFolderPairs()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, null, enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
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
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, "0 2 * * *", enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pairId = original!.FolderPairs.Single().Id;

            await _service.UpdateAsync(original.Id, "Photos", "new desc", "0 3 * * *", enabled: false,
                [new FolderPairInput(pairId, "Photos pair", @"C:\Pics", @"E:\Backup", WatchFolder: true, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.Name.Should().Be("Photos");
            profile.Description.Should().Be("new desc");
            profile.Schedule.Should().Be("0 3 * * *");
            profile.Type.Should().Be(ProfileType.FolderPair);
            profile.Enabled.Should().BeFalse();

            var pair = profile.FolderPairs.Should().ContainSingle().Subject;
            pair.Id.Should().Be(pairId); // matched pair updated in place, not replaced
            pair.SourceFolder.Should().Be(@"C:\Pics");
            pair.TargetFolder.Should().Be(@"E:\Backup");
            pair.WatchFolder.Should().BeTrue();
            pair.AllowDeletions.Should().BeTrue();
        }

        [Test]
        public async Task UpdateAsync_WritesOperationLogOfChangedFields()
        {
            await _service.CreateAsync("Docs", "desc", ProfileType.FolderPair, "0 2 * * *", enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pairId = original!.FolderPairs.Single().Id;

            // Change the name, enabled, and the folder pair's target; leave description/schedule alone.
            await _service.UpdateAsync(original.Id, "Photos", "desc", "0 2 * * *", enabled: false,
                [new FolderPairInput(pairId, "Src pair", @"C:\Src", @"E:\Backup", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.Include(l => l.Details)
                .SingleAsync(l => l.Name == "Profile updated: Docs");

            var messages = log.Details.OrderBy(d => d.Sequence).Select(d => d.Message).ToList();
            messages.Should().Contain("Name changed from 'Docs' to 'Photos'");
            messages.Should().Contain("Enabled changed from 'Yes' to 'No'");
            messages.Should().Contain(@"Folder pair 'Src pair' target changed from 'D:\Dst' to 'E:\Backup'");
            // Unchanged fields are not logged.
            messages.Should().NotContain(m => m.StartsWith("Description"));
            messages.Should().NotContain(m => m.StartsWith("Schedule"));
        }

        [Test]
        public async Task UpdateAsync_AddsAndRemovesFolderPairs()
        {
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, null, enabled: true,
            [
                new FolderPairInput(0, "Keep", @"C:\Keep", @"D:\Keep", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new FolderPairInput(0, "Drop", @"C:\Drop", @"D:\Drop", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var keepId = original!.FolderPairs.Single(p => p.SourceFolder == @"C:\Keep").Id;

            // Keep one (by id), drop the other (omit it), and add a new one (id 0).
            await _service.UpdateAsync(original.Id, "Docs", null, null, enabled: true,
            [
                new FolderPairInput(keepId, "Keep", @"C:\Keep", @"D:\Keep", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new FolderPairInput(0, "New", @"C:\New", @"D:\New", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();

            profile.FolderPairs.Select(p => p.SourceFolder).Should().BeEquivalentTo([@"C:\Keep", @"C:\New"]);
        }

        [Test]
        public async Task DeleteAsync_RemovesProfileAndFolderPairs()
        {
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, null, enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
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

        [Test]
        public async Task DeleteAsync_WritesOperationLog()
        {
            await _service.CreateAsync("Docs", "my docs", ProfileType.FolderPair, null, enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.DeleteAsync(id);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.Include(l => l.Details)
                .SingleAsync(l => l.Name == "Profile deleted: Docs");

            var messages = log.Details.Select(d => d.Message).ToList();
            messages.Should().Contain("Name: Docs");
            messages.Should().Contain("Description: my docs");
            messages.Should().Contain(m => m.StartsWith("Type:"));
        }

        [Test]
        public async Task CreateAsync_PersistsDisabledProfile()
        {
            // Guards the EF default-value sentinel: a false Enabled must still be stored as false
            // despite the column's store default of true.
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, null, enabled: false,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.SingleAsync();

            profile.Enabled.Should().BeFalse();
        }

        [Test]
        public async Task SetEnabledAsync_UpdatesOnlyTheEnabledFlag()
        {
            await _service.CreateAsync("Docs", null, ProfileType.FolderPair, null, enabled: true,
                [new FolderPairInput(0, "Src pair", @"C:\Src", @"D:\Dst", WatchFolder: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.SetEnabledAsync(id, false);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.FolderPairs).SingleAsync();
            profile.Enabled.Should().BeFalse();
            profile.Name.Should().Be("Docs"); // other fields untouched
            profile.FolderPairs.Should().ContainSingle();
        }

        [Test]
        public async Task SetEnabledAsync_IsNoOpWhenMissing()
        {
            var act = async () => await _service.SetEnabledAsync(999, false);

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
