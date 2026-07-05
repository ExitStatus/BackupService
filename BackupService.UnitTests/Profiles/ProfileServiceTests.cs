using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using BackupService.Scheduling;
using BackupService.UnitTests.Connections;
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
        private BackupService.UnitTests.Logging.TempLogStore _logStore = null!;

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
            _logStore = new BackupService.UnitTests.Logging.TempLogStore();
            _service = new ProfileService(
                factory.Object,
                new OperationLogFactory(factory.Object, _logStore.Store),
                new OneWaySyncItemService(),
                new InstantSyncItemService(),
                new ArchiveSyncItemService(new ReversibleProtector()),
                new LightroomArchiveItemService(),
                new TwoWaySyncItemService(),
                Mock.Of<IBackupScheduler>(),
                Mock.Of<IInstantSyncManager>(),
                Mock.Of<ILightroomArchiveManager>(),
                new ProfileStatusService());
        }

        [TearDown]
        public void TearDown()
        {
            _connection.Dispose();
            _logStore.Dispose();
        }

        [Test]
        public async Task CreateAsync_PersistsProfileWithOneOneWaySyncItem()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.AlwaysOverwrite)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();

            profile.Name.Should().Be("Docs");
            profile.Type.Should().Be(ProfileType.OneWaySync);
            profile.Schedule.Should().Be("0 2 * * *");
            profile.Enabled.Should().BeTrue();
            profile.DateCreated.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            profile.DateLastRun.Should().BeNull();

            var pair = profile.OneWaySyncItems.Should().ContainSingle().Subject;
            pair.SourceFolder.Should().Be(@"C:\Src");
            pair.TargetFolder.Should().Be(@"D:\Dst");
            pair.AllowDeletions.Should().BeTrue();
            pair.OverwriteBehaviour.Should().Be(OverwriteBehaviour.AlwaysOverwrite);
            pair.Status.Should().Be(OneWaySyncStatus.Idle);
            pair.LastRunStatus.Should().Be(OneWaySyncLastRunStatus.None);
        }

        [Test]
        public async Task CreateAsync_PersistsHandleMissedSync()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
                [new OneWaySyncInput(0, "P", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)],
                handleMissedSync: true);

            await using var context = new BackupDbContext(_options);
            context.Profiles.Single().HandleMissedSync.Should().BeTrue();
        }

        [Test]
        public async Task UpdateAsync_UpdatesHandleMissedSync()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
                [new OneWaySyncInput(0, "P", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pairId = original!.OneWaySyncItems.Single().Id;
            original.HandleMissedSync.Should().BeFalse();

            await _service.UpdateAsync(original.Id, "Docs", "0 2 * * *", enabled: true,
                [new OneWaySyncInput(pairId, "P", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)],
                handleMissedSync: true);

            await using var context = new BackupDbContext(_options);
            context.Profiles.Single().HandleMissedSync.Should().BeTrue();
        }

        [Test]
        public async Task CreateAsync_PersistsMultipleOneWaySyncItems()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
            [
                new OneWaySyncInput(0, "A", @"C:\A", @"D:\A", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new OneWaySyncInput(0, "B", @"C:\B", @"D:\B", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();

            profile.OneWaySyncItems.Select(p => p.SourceFolder).Should().BeEquivalentTo([@"C:\A", @"C:\B"]);
        }

        [Test]
        public async Task CreateAsync_PersistsOneWaySyncFilters()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "P", @"C:\A", @"D:\A", IncludeSubFolders: false, AllowDeletions: false,
                    OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer,
                    Filters:
                    [
                        new FilterInput(0, FilterDirection.Include, FilterKind.File, "*.txt"),
                        new FilterInput(0, FilterDirection.Exclude, FilterKind.Folder, "bin"),
                    ])]);

            await using var context = new BackupDbContext(_options);
            var pair = await context.OneWaySyncItems.Include(p => p.Filters).SingleAsync();

            pair.Filters.Should().HaveCount(2);
            pair.Filters.Should().ContainSingle(f => f.Direction == FilterDirection.Include && f.Kind == FilterKind.File && f.Pattern == "*.txt");
            pair.Filters.Should().ContainSingle(f => f.Direction == FilterDirection.Exclude && f.Kind == FilterKind.Folder && f.Pattern == "bin");
        }

        [Test]
        public async Task UpdateAsync_ReconcilesOneWaySyncFilters()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "P", @"C:\A", @"D:\A", IncludeSubFolders: false, AllowDeletions: false,
                    OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer,
                    Filters:
                    [
                        new FilterInput(0, FilterDirection.Include, FilterKind.File, "*.txt"),
                        new FilterInput(0, FilterDirection.Exclude, FilterKind.File, "*.tmp"),
                    ])]);

            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pair = original!.OneWaySyncItems.Single();
            var keepId = pair.Filters.Single(f => f.Pattern == "*.txt").Id;

            // Keep the *.txt rule (update its pattern), drop *.tmp, add a new exclude folder.
            await _service.UpdateAsync(original.Id, "Docs", null, enabled: true,
                [new OneWaySyncInput(pair.Id, "P", @"C:\A", @"D:\A", IncludeSubFolders: false, AllowDeletions: false,
                    OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer,
                    Filters:
                    [
                        new FilterInput(keepId, FilterDirection.Include, FilterKind.File, "*.md"),
                        new FilterInput(0, FilterDirection.Exclude, FilterKind.Folder, "obj"),
                    ])]);

            await using var context = new BackupDbContext(_options);
            var saved = await context.OneWaySyncItems.Include(p => p.Filters).SingleAsync();

            saved.Filters.Should().HaveCount(2);
            saved.Filters.Should().ContainSingle(f => f.Id == keepId && f.Pattern == "*.md"); // updated in place
            saved.Filters.Should().ContainSingle(f => f.Direction == FilterDirection.Exclude && f.Pattern == "obj");
            saved.Filters.Should().NotContain(f => f.Pattern == "*.tmp"); // removed
        }

        [Test]
        public async Task CreateAsync_WritesOperationLogWithProfileDetails()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
            [
                new OneWaySyncInput(0, "Pair A", @"C:\A", @"D:\A", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.AlwaysOverwrite),
                new OneWaySyncInput(0, "Pair B", @"C:\B", @"D:\B", IncludeSubFolders: false, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs.SingleAsync();

            log.Name.Should().Be("Profile created: Docs");
            log.ProfileId.Should().Be(await GetOnlyProfileIdAsync());

            var messages = (await _logStore.Store.ReadAsync(log.Id)).Select(d => d.Message).ToList();
            messages.Should().Contain("Name: Docs");
            messages.Should().Contain(m => m.StartsWith("Type:"));
            messages.Should().Contain(m => m.StartsWith("Schedule:"));
            messages.Should().Contain("Enabled: Yes");
            // Each folder-pair detail is its own row, not one delimited string.
            messages.Should().Contain("One way sync: Pair A");
            messages.Should().Contain(@"Source: C:\A");
            messages.Should().Contain(@"Target: D:\A");
            messages.Should().Contain("One way sync: Pair B");
            messages.Should().Contain("Allow deletions: Yes");
        }

        [Test]
        public async Task GetAsync_ReturnsProfileWithOneWaySyncItems()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            var profile = await _service.GetAsync(id);

            profile.Should().NotBeNull();
            profile!.Name.Should().Be("Docs");
            profile.OneWaySyncItems.Should().ContainSingle().Which.SourceFolder.Should().Be(@"C:\Src");
        }

        [Test]
        public async Task GetAsync_ReturnsNullWhenMissing()
        {
            (await _service.GetAsync(999)).Should().BeNull();
        }

        [Test]
        public async Task UpdateAsync_UpdatesProfileAndOneWaySyncItemButNotType()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pairId = original!.OneWaySyncItems.Single().Id;

            await _service.UpdateAsync(original.Id, "Photos", "0 3 * * *", enabled: false,
                [new OneWaySyncInput(pairId, "Photos pair", @"C:\Pics", @"E:\Backup", IncludeSubFolders: false, AllowDeletions: true, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();

            profile.Name.Should().Be("Photos");
            profile.Schedule.Should().Be("0 3 * * *");
            profile.Type.Should().Be(ProfileType.OneWaySync);
            profile.Enabled.Should().BeFalse();

            var pair = profile.OneWaySyncItems.Should().ContainSingle().Subject;
            pair.Id.Should().Be(pairId); // matched pair updated in place, not replaced
            pair.SourceFolder.Should().Be(@"C:\Pics");
            pair.TargetFolder.Should().Be(@"E:\Backup");
            pair.AllowDeletions.Should().BeTrue();
        }

        [Test]
        public async Task UpdateAsync_WritesOperationLogOfChangedFields()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, "0 2 * * *", enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var pairId = original!.OneWaySyncItems.Single().Id;

            // Change the name, enabled, and the one way sync's target; leave description/schedule alone.
            await _service.UpdateAsync(original.Id, "Photos", "0 2 * * *", enabled: false,
                [new OneWaySyncInput(pairId, "Src pair", @"C:\Src", @"E:\Backup", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs
                .SingleAsync(l => l.Name == "Profile updated: Docs");

            var messages = (await _logStore.Store.ReadAsync(log.Id)).Select(d => d.Message).ToList();
            messages.Should().Contain("Name changed from 'Docs' to 'Photos'");
            messages.Should().Contain("Enabled changed from 'Yes' to 'No'");
            messages.Should().Contain(@"One way sync 'Src pair' target changed from 'D:\Dst' to 'E:\Backup'");
            // Unchanged fields are not logged.
            messages.Should().NotContain(m => m.StartsWith("Schedule"));
        }

        [Test]
        public async Task UpdateAsync_AddsAndRemovesOneWaySyncItems()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
            [
                new OneWaySyncInput(0, "Keep", @"C:\Keep", @"D:\Keep", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new OneWaySyncInput(0, "Drop", @"C:\Drop", @"D:\Drop", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);
            var original = await _service.GetAsync(await GetOnlyProfileIdAsync());
            var keepId = original!.OneWaySyncItems.Single(p => p.SourceFolder == @"C:\Keep").Id;

            // Keep one (by id), drop the other (omit it), and add a new one (id 0).
            await _service.UpdateAsync(original.Id, "Docs", null, enabled: true,
            [
                new OneWaySyncInput(keepId, "Keep", @"C:\Keep", @"D:\Keep", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
                new OneWaySyncInput(0, "New", @"C:\New", @"D:\New", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer),
            ]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();

            profile.OneWaySyncItems.Select(p => p.SourceFolder).Should().BeEquivalentTo([@"C:\Keep", @"C:\New"]);
        }

        [Test]
        public async Task DeleteAsync_RemovesProfileAndOneWaySyncItems()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.DeleteAsync(id);

            await using var context = new BackupDbContext(_options);
            (await context.Profiles.CountAsync()).Should().Be(0);
            (await context.OneWaySyncItems.CountAsync()).Should().Be(0);
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
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.DeleteAsync(id);

            await using var context = new BackupDbContext(_options);

            // The "Profile created" log was associated with the profile and is cascade-deleted
            // with it; only the unassociated deletion log survives.
            (await context.OperationLogs.CountAsync()).Should().Be(1);
            var log = await context.OperationLogs
                .SingleAsync(l => l.Name == "Profile deleted: Docs");
            log.ProfileId.Should().BeNull();

            var messages = (await _logStore.Store.ReadAsync(log.Id)).Select(d => d.Message).ToList();
            messages.Should().Contain("Name: Docs");
            messages.Should().Contain(m => m.StartsWith("Type:"));
        }

        [Test]
        public async Task CreateAsync_PersistsDisabledProfile()
        {
            // Guards the EF default-value sentinel: a false Enabled must still be stored as false
            // despite the column's store default of true.
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: false,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.SingleAsync();

            profile.Enabled.Should().BeFalse();
        }

        [Test]
        public async Task SetEnabledAsync_UpdatesOnlyTheEnabledFlag()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.SetEnabledAsync(id, false);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.OneWaySyncItems).SingleAsync();
            profile.Enabled.Should().BeFalse();
            profile.Name.Should().Be("Docs"); // other fields untouched
            profile.OneWaySyncItems.Should().ContainSingle();
        }

        [Test]
        public async Task SetEnabledAsync_WritesOperationLogAssociatedWithProfile()
        {
            await _service.CreateAsync("Docs", ProfileType.OneWaySync, null, enabled: true,
                [new OneWaySyncInput(0, "Src pair", @"C:\Src", @"D:\Dst", IncludeSubFolders: false, AllowDeletions: false, OverwriteBehaviour: OverwriteBehaviour.DoNotOverwriteNewer)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.SetEnabledAsync(id, false);

            await using var context = new BackupDbContext(_options);
            var log = await context.OperationLogs
                .SingleAsync(l => l.Name == "Profile Docs was disabled");
            log.ProfileId.Should().Be(id);
            (await _logStore.Store.ReadAsync(log.Id)).Should().BeEmpty(); // self-describing: message in the name, no lines
        }

        [Test]
        public async Task SetEnabledAsync_IsNoOpWhenMissing()
        {
            var act = async () => await _service.SetEnabledAsync(999, false);

            await act.Should().NotThrowAsync();
        }

        [Test]
        public async Task CreateAsync_PersistsInstantSyncProfileWithItems_AndNotifiesManager()
        {
            var manager = new Mock<IInstantSyncManager>();
            var factory = new Mock<IDatabaseContextFactory>();
            factory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            var service = new ProfileService(
                factory.Object,
                new OperationLogFactory(factory.Object, _logStore.Store),
                new OneWaySyncItemService(),
                new InstantSyncItemService(),
                new ArchiveSyncItemService(new ReversibleProtector()),
                new LightroomArchiveItemService(),
                new TwoWaySyncItemService(),
                Mock.Of<IBackupScheduler>(),
                manager.Object,
                Mock.Of<ILightroomArchiveManager>(),
                new ProfileStatusService());

            await service.CreateAsync(
                "Live", ProfileType.InstantSync, scheduleCron: null, enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: [new InstantSyncInput(0, "Item", @"C:\Src", @"D:\Dst", DebounceMilliseconds: 2000, IncludeSubFolders: true, AllowDeletions: true)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.InstantSyncItems).SingleAsync();

            profile.Type.Should().Be(ProfileType.InstantSync);
            profile.Schedule.Should().BeNull();
            var item = profile.InstantSyncItems.Should().ContainSingle().Subject;
            item.SourceFolder.Should().Be(@"C:\Src");
            item.TargetFolder.Should().Be(@"D:\Dst");
            item.DebounceMilliseconds.Should().Be(2000);
            item.IncludeSubFolders.Should().BeTrue();
            item.AllowDeletions.Should().BeTrue();

            manager.Verify(m => m.SyncAsync(profile.Id, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateAsync_SyncsInstantSyncItems()
        {
            await _service.CreateAsync(
                "Live", ProfileType.InstantSync, scheduleCron: null, enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: [new InstantSyncInput(0, "Item", @"C:\Src", @"D:\Dst", DebounceMilliseconds: 1000, IncludeSubFolders: false, AllowDeletions: false)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.UpdateAsync(
                id, "Live", scheduleCron: null, enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: [new InstantSyncInput(0, "Item2", @"C:\Src2", @"D:\Dst2", DebounceMilliseconds: 3000, IncludeSubFolders: true, AllowDeletions: true)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.InstantSyncItems).SingleAsync();
            var item = profile.InstantSyncItems.Should().ContainSingle().Subject;
            item.Name.Should().Be("Item2");
            item.DebounceMilliseconds.Should().Be(3000);
        }

        [Test]
        public async Task CreateAsync_PersistsArchiveSyncProfileWithItems()
        {
            await _service.CreateAsync(
                "Nightly", ProfileType.ArchiveSync, "0 2 * * *", enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: null,
                archiveSyncItems: [new ArchiveSyncInput(0, "Docs", @"C:\Src", @"D:\Archives", "DocsBackup", IncludeSubFolders: true, OnlyCopyOnChange: false, CompressionLevel: ArchiveCompressionLevel.Optimal, PasswordProtect: false, Password: null, EncryptionMethod: ArchiveEncryptionMethod.Aes256, RetentionMode: ArchiveRetentionMode.GrandfatherFatherSon, RetentionCount: 3, MaxLevels: 3)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.ArchiveSyncItems).SingleAsync();

            profile.Type.Should().Be(ProfileType.ArchiveSync);
            profile.Schedule.Should().Be("0 2 * * *");
            var item = profile.ArchiveSyncItems.Should().ContainSingle().Subject;
            item.SourceFolder.Should().Be(@"C:\Src");
            item.TargetFolder.Should().Be(@"D:\Archives");
            item.FileName.Should().Be("DocsBackup");
            item.IncludeSubFolders.Should().BeTrue();
            item.RetentionMode.Should().Be(ArchiveRetentionMode.GrandfatherFatherSon);
            item.RetentionCount.Should().Be(3);
            item.MaxLevels.Should().Be(3);
            item.RunCount.Should().Be(0);
        }

        [Test]
        public async Task UpdateAsync_SyncsArchiveSyncItems()
        {
            await _service.CreateAsync(
                "Nightly", ProfileType.ArchiveSync, "0 2 * * *", enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: null,
                archiveSyncItems: [new ArchiveSyncInput(0, "Docs", @"C:\Src", @"D:\Archives", "DocsBackup", IncludeSubFolders: false, OnlyCopyOnChange: false, CompressionLevel: ArchiveCompressionLevel.Optimal, PasswordProtect: false, Password: null, EncryptionMethod: ArchiveEncryptionMethod.Aes256, RetentionMode: ArchiveRetentionMode.KeepLastN, RetentionCount: 5, MaxLevels: 1)]);
            var id = await GetOnlyProfileIdAsync();

            await _service.UpdateAsync(
                id, "Nightly", "0 3 * * *", enabled: true,
                oneWaySyncItems: [],
                instantSyncItems: null,
                archiveSyncItems: [new ArchiveSyncInput(0, "Pics", @"C:\Pics", @"D:\Archives2", "PicsBackup", IncludeSubFolders: true, OnlyCopyOnChange: false, CompressionLevel: ArchiveCompressionLevel.Optimal, PasswordProtect: false, Password: null, EncryptionMethod: ArchiveEncryptionMethod.Aes256, RetentionMode: ArchiveRetentionMode.KeepLastN, RetentionCount: 10, MaxLevels: 1)]);

            await using var context = new BackupDbContext(_options);
            var profile = await context.Profiles.Include(p => p.ArchiveSyncItems).SingleAsync();
            var item = profile.ArchiveSyncItems.Should().ContainSingle().Subject;
            item.Name.Should().Be("Pics");
            item.FileName.Should().Be("PicsBackup");
            item.RetentionCount.Should().Be(10);
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
        public async Task GetSummariesAsync_ReturnsIdAndNameOrderedByName()
        {
            await SeedProfilesAsync("Banana", "Apple", "Cherry");

            var summaries = await _service.GetSummariesAsync();

            summaries.Select(s => s.Name).Should().ContainInOrder("Apple", "Banana", "Cherry");
            summaries.Should().OnlyContain(s => s.Id > 0);
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

        [Test]
        public async Task GetPageAsync_FiltersByType()
        {
            await SeedTypedProfilesAsync(
                ("FP1", ProfileType.OneWaySync),
                ("FP2", ProfileType.OneWaySync),
                ("Live", ProfileType.InstantSync),
                ("Zip", ProfileType.ArchiveSync));

            var oneWaySyncItems = await _service.GetPageAsync(1, 10, ProfileSortColumn.Name, descending: false, ProfileType.OneWaySync);

            oneWaySyncItems.TotalCount.Should().Be(2);
            oneWaySyncItems.Items.Select(p => p.Name).Should().BeEquivalentTo(["FP1", "FP2"]);
        }

        [Test]
        public async Task GetPageAsync_FiltersByConnection_MatchingSourceOrTarget()
        {
            int connA;
            await using (var context = new BackupDbContext(_options))
            {
                var a = new Connection { Name = "Drive A", Type = ConnectionType.Usb, DateCreated = DateTimeOffset.UtcNow };
                var b = new Connection { Name = "Drive B", Type = ConnectionType.Usb, DateCreated = DateTimeOffset.UtcNow };
                context.Connections.AddRange(a, b);
                context.Profiles.AddRange(
                    new Profile { Name = "Src-A", Type = ProfileType.OneWaySync, DateCreated = DateTimeOffset.UtcNow, SourceConnection = a },
                    new Profile { Name = "Tgt-A", Type = ProfileType.OneWaySync, DateCreated = DateTimeOffset.UtcNow, TargetConnection = a },
                    new Profile { Name = "Uses-B", Type = ProfileType.OneWaySync, DateCreated = DateTimeOffset.UtcNow, SourceConnection = b },
                    new Profile { Name = "Local", Type = ProfileType.OneWaySync, DateCreated = DateTimeOffset.UtcNow });
                await context.SaveChangesAsync();
                connA = a.Id;
            }

            var page = await _service.GetPageAsync(1, 10, ProfileSortColumn.Name, descending: false, connectionId: connA);

            page.TotalCount.Should().Be(2);
            page.Items.Select(p => p.Name).Should().BeEquivalentTo(["Src-A", "Tgt-A"]);
        }

        [Test]
        public async Task GetCountsByTypeAsync_ReturnsPerTypeCountsAndOmitsTypesWithNone()
        {
            await SeedTypedProfilesAsync(
                ("FP1", ProfileType.OneWaySync),
                ("FP2", ProfileType.OneWaySync),
                ("Live", ProfileType.InstantSync));

            var counts = await _service.GetCountsByTypeAsync();

            counts[ProfileType.OneWaySync].Should().Be(2);
            counts[ProfileType.InstantSync].Should().Be(1);
            counts.ContainsKey(ProfileType.ArchiveSync).Should().BeFalse();
            counts.ContainsKey(ProfileType.LightroomArchive).Should().BeFalse();
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

        private async Task SeedTypedProfilesAsync(params (string Name, ProfileType Type)[] profiles)
        {
            await using var context = new BackupDbContext(_options);
            foreach (var (name, type) in profiles)
            {
                context.Profiles.Add(new Profile { Name = name, Type = type, DateCreated = DateTimeOffset.UtcNow });
            }
            await context.SaveChangesAsync();
        }
    }
}
