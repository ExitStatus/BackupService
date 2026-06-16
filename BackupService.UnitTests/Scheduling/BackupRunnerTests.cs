using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using BackupService.Scheduling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class BackupRunnerTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private OperationLogFactory _logFactory = null!;
        private ProfileStatusService _statusService = null!;

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
            _logFactory = new OperationLogFactory(_dbFactory);
            _statusService = new ProfileStatusService();
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private int SeedProfile(ProfileType type = ProfileType.FolderPair)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = "Nightly",
                Type = type,
                DateCreated = DateTimeOffset.UtcNow,
                FolderPairs =
                {
                    new FolderPair { Name = "Docs", SourceFolder = @"C:\src", TargetFolder = @"D:\dst" },
                    new FolderPair { Name = "Pics", SourceFolder = @"C:\pics", TargetFolder = @"D:\pics" },
                },
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        [Test]
        public async Task RunAsync_DispatchesToMatchingHandler_WithFolderPairsLoaded()
        {
            var id = SeedProfile();
            var handler = new CapturingHandler();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            handler.Calls.Should().Be(1);
            handler.Captured.Should().NotBeNull();
            handler.Captured!.FolderPairs.Select(p => p.Name).Should().BeEquivalentTo("Docs", "Pics");
        }

        [Test]
        public async Task RunAsync_OnSuccess_SetsIdleAndStampsLastRun()
        {
            var id = SeedProfile();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)new CapturingHandler() }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            _statusService.Get(id).Should().Be(ProfileStatus.Idle);

            await using var db = new BackupDbContext(_options);
            var profile = await db.Profiles.SingleAsync();
            profile.DateLastRun.Should().NotBeNull();

            // The runner no longer writes its own log; the handler owns the single per-run log.
            (await db.OperationLogs.AnyAsync()).Should().BeFalse();
        }

        [Test]
        public async Task RunAsync_WhenHandlerThrows_StampsLastRunAndDoesNotThrow()
        {
            var id = SeedProfile();
            var handler = new CapturingHandler { OnHandle = _ => throw new InvalidOperationException("boom") };
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().NotBeNull();
        }

        [Test]
        public async Task RunAsync_WhenNoHandlerForType_LogsErrorAndDoesNotThrow()
        {
            var id = SeedProfile();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, Array.Empty<IProfileTypeHandler>(), NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            await using var db = new BackupDbContext(_options);
            (await db.OperationLogs.AnyAsync(l => l.Name == "Scheduled backup failed: Nightly")).Should().BeTrue();
        }

        [Test]
        public async Task RunAsync_WhenAlreadyRunning_SkipsWithoutCallingHandler()
        {
            var id = SeedProfile();
            _statusService.Set(id, ProfileStatus.Running); // a run is already in progress
            var handler = new CapturingHandler();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            handler.Calls.Should().Be(0);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().BeNull();
        }

        [Test]
        public async Task RunAsync_WhenProfileLocked_SkipsWithoutRunningOrChangingStatus()
        {
            var id = SeedProfile();
            _statusService.Lock(id);
            var handler = new CapturingHandler();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            handler.Calls.Should().Be(0);
            _statusService.Get(id).Should().Be(ProfileStatus.Idle);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().BeNull();
        }

        [Test]
        public async Task RunAsync_WhenProfileMissing_IsNoOp()
        {
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new[] { (IProfileTypeHandler)new CapturingHandler() }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(999);

            await using var db = new BackupDbContext(_options);
            (await db.OperationLogs.AnyAsync()).Should().BeFalse();
        }

        private sealed class CapturingHandler : IProfileTypeHandler
        {
            public ProfileType Type => ProfileType.FolderPair;

            public int Calls { get; private set; }

            public Profile? Captured { get; private set; }

            public Func<Profile, Task>? OnHandle { get; init; }

            public async Task HandleAsync(Profile profile, CancellationToken cancellationToken)
            {
                Calls++;
                Captured = profile;
                if (OnHandle is not null)
                {
                    await OnHandle(profile);
                }
            }
        }
    }
}
