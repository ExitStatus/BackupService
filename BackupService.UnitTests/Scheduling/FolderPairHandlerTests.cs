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
    public class FolderPairHandlerTests
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
                    Name = "Docs",
                    Type = ProfileType.FolderPair,
                    DateCreated = DateTimeOffset.UtcNow,
                    FolderPairs = { new FolderPair { Name = "P", SourceFolder = @"C:\a", TargetFolder = @"D:\b" } },
                });
                db.SaveChanges();
            }

            await using var load = new BackupDbContext(_options);
            return await load.Profiles.Include(p => p.FolderPairs).SingleAsync();
        }

        private FolderPairHandler Handler(IFolderPairSynchronizer synchronizer) =>
            new(new OperationLogFactory(_dbFactory), synchronizer, _dbFactory, _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<FolderPairHandler>.Instance);

        [Test]
        public async Task HandleAsync_RunsEachPair_WritesSummaryAndPersistsSuccess()
        {
            var profile = await SeedAndLoadProfileAsync();
            var synchronizer = new FakeSynchronizer(new BackupResult { Copied = 2 });

            await Handler(synchronizer).HandleAsync(profile, manual: false, CancellationToken.None);

            synchronizer.SyncedPairNames.Should().Equal("P");

            await using var verify = new BackupDbContext(_options);

            // Exactly one log, summarised with counts at Info level.
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Folder Pairs Handler ran successfully in");
            log.Name.Should().Contain("2 copied");
            log.Level.Should().Be(OperationLogLevel.Info);
            log.ProfileId.Should().Be(profile.Id);

            // The pair header line was written under that same log.
            var details = await verify.OperationLogDetails.Where(d => d.OperationLogId == log.Id).ToListAsync();
            details.Should().ContainSingle(d => d.Message == @"Folder pair 'P': C:\a -> D:\b");

            // Per-pair status persisted.
            var pair = await verify.FolderPairs.SingleAsync();
            pair.Status.Should().Be(FolderPairStatus.Idle);
            pair.LastRunStatus.Should().Be(FolderPairLastRunStatus.Success);
        }

        [Test]
        public async Task HandleAsync_WhenManual_PrefixesLogWithManualTag()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeSynchronizer(new BackupResult())).HandleAsync(profile, manual: true, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("[Manual] Folder Pairs Handler ran successfully in");
        }

        [Test]
        public async Task HandleAsync_WhenSyncReportsErrors_SummaryAndPairStatusReflectFailure()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeSynchronizer(new BackupResult { Copied = 1, Errors = 2 }))
                .HandleAsync(profile, manual: false, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Folder Pairs Handler completed with 2 error(s) in");
            log.Level.Should().Be(OperationLogLevel.Error);

            (await verify.FolderPairs.SingleAsync()).LastRunStatus.Should().Be(FolderPairLastRunStatus.Fail);
        }

        [Test]
        public async Task HandleAsync_OnFatalException_SetsErrorSummaryAndStatusAndRethrows()
        {
            // Make a detail write throw so the handler's try-body fails after the log is created.
            var loggerMock = new Mock<IOperationLogger>();
            loggerMock.Setup(l => l.AppendAsync(It.IsAny<string[]>())).ThrowsAsync(new InvalidOperationException("boom"));

            var logFactoryMock = new Mock<IOperationLogFactory>();
            logFactoryMock
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(loggerMock.Object);

            var profile = new Profile
            {
                Id = 5,
                Name = "Docs",
                Type = ProfileType.FolderPair,
                FolderPairs = { new FolderPair { Name = "P", SourceFolder = @"C:\a", TargetFolder = @"D:\b" } },
            };
            var handler = new FolderPairHandler(
                logFactoryMock.Object, new FakeSynchronizer(new BackupResult()), _dbFactory, _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<FolderPairHandler>.Instance);

            var act = () => handler.HandleAsync(profile, manual: false, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
            _statusService.Get(5).Should().Be(ProfileStatus.Error);

            loggerMock.Verify(
                l => l.SetSummaryAsync(
                    It.Is<string>(s => s.StartsWith("Folder Pairs Handler failed in")),
                    OperationLogLevel.Error),
                Times.Once);
        }

        private sealed class FakeSynchronizer(BackupResult result) : IFolderPairSynchronizer
        {
            public List<string> SyncedPairNames { get; } = [];

            public Task<BackupResult> SyncAsync(FolderPair pair, IOperationLogger log, CancellationToken cancellationToken)
            {
                SyncedPairNames.Add(pair.Name);
                return Task.FromResult(result);
            }
        }
    }
}
