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
    public class InstantSyncHandlerTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
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
            _statusService = new ProfileStatusService();
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private async Task<Profile> SeedAndLoadProfileAsync()
        {
            using (var db = new BackupDbContext(_options))
            {
                db.Profiles.Add(new Profile
                {
                    Name = "Live",
                    Type = ProfileType.InstantSync,
                    DateCreated = DateTimeOffset.UtcNow,
                    InstantSyncItems = { new InstantSyncItem { Name = "I", SourceFolder = @"C:\a", TargetFolder = @"D:\b", DebounceMilliseconds = 1000 } },
                });
                db.SaveChanges();
            }

            await using var load = new BackupDbContext(_options);
            return await load.Profiles.Include(p => p.InstantSyncItems).SingleAsync();
        }

        private InstantSyncHandler Handler(IFolderPairSynchronizer synchronizer) =>
            new(new OperationLogFactory(_dbFactory), synchronizer, _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<InstantSyncHandler>.Instance);

        [Test]
        public async Task HandleAsync_RunsEachItem_WritesSummaryWithCounts()
        {
            var profile = await SeedAndLoadProfileAsync();
            var synchronizer = new FakeSynchronizer(new BackupResult { Copied = 2 });

            await Handler(synchronizer).HandleAsync(profile, manual: false, CancellationToken.None);

            synchronizer.SyncedPairNames.Should().Equal("I");

            await using var verify = new BackupDbContext(_options);

            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Instant Sync Handler ran successfully in");
            log.Name.Should().Contain("2 copied");
            log.Level.Should().Be(OperationLogLevel.Info);
            log.ProfileId.Should().Be(profile.Id);

            var details = await verify.OperationLogDetails.Where(d => d.OperationLogId == log.Id).ToListAsync();
            details.Should().ContainSingle(d => d.Message == @"Instant sync 'I': C:\a -> D:\b");
        }

        [Test]
        public async Task HandleAsync_WhenManual_PrefixesLogWithManualTag()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeSynchronizer(new BackupResult())).HandleAsync(profile, manual: true, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("[Manual] Instant Sync Handler ran successfully in");
        }

        [Test]
        public async Task HandleAsync_WhenSyncReportsErrors_SummaryReflectsFailure()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeSynchronizer(new BackupResult { Copied = 1, Errors = 2 }))
                .HandleAsync(profile, manual: false, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Instant Sync Handler completed with 2 error(s) in");
            log.Level.Should().Be(OperationLogLevel.Error);
        }

        [Test]
        public async Task HandleAsync_OnFatalException_SetsErrorSummaryAndStatusAndRethrows()
        {
            var loggerMock = new Mock<IOperationLogger>();
            loggerMock.Setup(l => l.AppendAsync(It.IsAny<string[]>())).ThrowsAsync(new InvalidOperationException("boom"));

            var logFactoryMock = new Mock<IOperationLogFactory>();
            logFactoryMock
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(loggerMock.Object);

            var profile = new Profile
            {
                Id = 5,
                Name = "Live",
                Type = ProfileType.InstantSync,
                InstantSyncItems = { new InstantSyncItem { Name = "I", SourceFolder = @"C:\a", TargetFolder = @"D:\b", DebounceMilliseconds = 1000 } },
            };
            var handler = new InstantSyncHandler(
                logFactoryMock.Object, new FakeSynchronizer(new BackupResult()), _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<InstantSyncHandler>.Instance);

            var act = () => handler.HandleAsync(profile, manual: false, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
            _statusService.Get(5).Should().Be(ProfileStatus.Error);

            loggerMock.Verify(
                l => l.SetSummaryAsync(
                    It.Is<string>(s => s.StartsWith("Instant Sync Handler failed in")),
                    OperationLogLevel.Error),
                Times.Once);
        }

        private sealed class FakeSynchronizer(BackupResult result) : IFolderPairSynchronizer
        {
            public List<string> SyncedPairNames { get; } = [];

            public Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken, IProgress<int>? fileProgress = null)
            {
                SyncedPairNames.Add(pair.Name);
                return Task.FromResult(result);
            }

            public Task<int> CountFilesAsync(FolderPair pair, CancellationToken cancellationToken) => Task.FromResult(0);
        }
    }
}
