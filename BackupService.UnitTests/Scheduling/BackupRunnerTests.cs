using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using BackupService.Scheduling;
using BackupService.Scheduling.Usb;
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
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            handler.Calls.Should().Be(1);
            handler.Captured.Should().NotBeNull();
            handler.Captured!.FolderPairs.Select(p => p.Name).Should().BeEquivalentTo("Docs", "Pics");
            handler.LastManual.Should().BeFalse(); // scheduled run by default
        }

        [Test]
        public async Task RunAsync_PassesManualFlagToHandler()
        {
            var id = SeedProfile();
            var handler = new CapturingHandler();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id, manual: true);

            handler.LastManual.Should().BeTrue();
        }

        [Test]
        public async Task RunAsync_OnSuccess_SetsIdleAndStampsLastRun()
        {
            var id = SeedProfile();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)new CapturingHandler() }, NullLogger<BackupRunner>.Instance);

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
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().NotBeNull();
        }

        [Test]
        public async Task RunAsync_WhenNoHandlerForType_LogsErrorAndDoesNotThrow()
        {
            var id = SeedProfile();
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),Array.Empty<IProfileTypeHandler>(), NullLogger<BackupRunner>.Instance);

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
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

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
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(id);

            handler.Calls.Should().Be(0);
            _statusService.Get(id).Should().Be(ProfileStatus.Idle);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().BeNull();
        }

        [Test]
        public async Task RunAsync_WhenProfileMissing_IsNoOp()
        {
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)new CapturingHandler() }, NullLogger<BackupRunner>.Instance);

            await runner.RunAsync(999);

            await using var db = new BackupDbContext(_options);
            (await db.OperationLogs.AnyAsync()).Should().BeFalse();
        }

        [Test]
        public void RequestStop_WhenNothingRunning_ReturnsFalse()
        {
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)new CapturingHandler() }, NullLogger<BackupRunner>.Instance);

            runner.RequestStop(123).Should().BeFalse();
        }

        [Test]
        public async Task RequestStop_CancelsRunningHandler_AndSettlesToIdle()
        {
            var id = SeedProfile();
            var started = new TaskCompletionSource();
            var handler = new CapturingHandler
            {
                // Block until cancelled, throwing OperationCanceledException like a real stopped run.
                OnHandleWithToken = async (_, ct) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                },
            };
            var runner = new BackupRunner(_dbFactory, _logFactory, _statusService, new UsbRunGate(),new[] { (IProfileTypeHandler)handler }, NullLogger<BackupRunner>.Instance);

            var run = runner.RunAsync(id, manual: true);
            await started.Task; // the run has registered its cancellation source

            runner.RequestStop(id).Should().BeTrue();

            // RunAsync swallows the cancellation (a stop is not a failure) and completes.
            await run;

            // Cancelled runs go back to Idle (not Error) so the profile waits for its next scheduled run.
            _statusService.Get(id).Should().Be(ProfileStatus.Idle);

            await using var db = new BackupDbContext(_options);
            (await db.Profiles.SingleAsync()).DateLastRun.Should().NotBeNull();
        }

        private sealed class CapturingHandler : IProfileTypeHandler
        {
            public ProfileType Type => ProfileType.FolderPair;

            public int Calls { get; private set; }

            public Profile? Captured { get; private set; }

            public Func<Profile, Task>? OnHandle { get; init; }

            public Func<Profile, CancellationToken, Task>? OnHandleWithToken { get; init; }

            public bool LastManual { get; private set; }

            public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
            {
                Calls++;
                Captured = profile;
                LastManual = manual;
                if (OnHandle is not null)
                {
                    await OnHandle(profile);
                }
                if (OnHandleWithToken is not null)
                {
                    await OnHandleWithToken(profile, cancellationToken);
                }
            }
        }
    }
}
