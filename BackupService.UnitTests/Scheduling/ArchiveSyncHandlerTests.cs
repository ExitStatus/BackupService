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
    public class ArchiveSyncHandlerTests
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

        private async Task<Profile> SeedAndLoadProfileAsync(int runCount = 0)
        {
            using (var db = new BackupDbContext(_options))
            {
                db.Profiles.Add(new Profile
                {
                    Name = "Nightly",
                    Type = ProfileType.ArchiveSync,
                    Schedule = "0 2 * * *",
                    DateCreated = DateTimeOffset.UtcNow,
                    ArchiveSyncItems =
                    {
                        new ArchiveSyncItem
                        {
                            Name = "Docs", SourceFolder = @"C:\a", TargetFolder = @"D:\b", FileName = "DocsBackup",
                            RetentionMode = ArchiveRetentionMode.KeepLastN, RetentionCount = 5, MaxLevels = 1, RunCount = runCount,
                        },
                    },
                });
                db.SaveChanges();
            }

            await using var load = new BackupDbContext(_options);
            return await load.Profiles.Include(p => p.ArchiveSyncItems).SingleAsync();
        }

        private ArchiveSyncHandler Handler(IArchiveSyncProcessor processor) =>
            new(new OperationLogFactory(_dbFactory), processor, _dbFactory, _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<ArchiveSyncHandler>.Instance);

        [Test]
        public async Task HandleAsync_RunsEachItem_WritesSummaryWithCounts_AndAdvancesRunCount()
        {
            var profile = await SeedAndLoadProfileAsync();
            var processor = new FakeProcessor(new BackupResult { Copied = 1, Deleted = 2 });

            await Handler(processor).HandleAsync(profile, manual: false, CancellationToken.None);

            processor.ItemNames.Should().Equal("Docs");
            processor.RunIndexes.Should().Equal(1L);

            await using var verify = new BackupDbContext(_options);

            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Archive Sync Handler ran successfully in");
            log.Name.Should().Contain("1 archive(s) created");
            log.Name.Should().Contain("2 pruned");
            log.Level.Should().Be(OperationLogLevel.Info);
            log.ProfileId.Should().Be(profile.Id);

            // RunCount advanced because an archive was created.
            var item = await verify.ArchiveSyncItems.SingleAsync();
            item.RunCount.Should().Be(1);
        }

        [Test]
        public async Task HandleAsync_WhenManual_PrefixesLogWithManualTag()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeProcessor(new BackupResult { Copied = 1 })).HandleAsync(profile, manual: true, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("[Manual] Archive Sync Handler ran successfully in");
        }

        [Test]
        public async Task HandleAsync_WhenProcessorReportsErrors_SummaryReflectsFailure_AndRunCountNotAdvanced()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeProcessor(new BackupResult { Copied = 0, Errors = 1 }))
                .HandleAsync(profile, manual: false, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Archive Sync Handler completed with 1 error(s) in");
            log.Level.Should().Be(OperationLogLevel.Error);

            // No archive created → run counter stays put (keeps the GFS cadence aligned to real archives).
            var item = await verify.ArchiveSyncItems.SingleAsync();
            item.RunCount.Should().Be(0);
        }

        [Test]
        public async Task HandleAsync_WhenCancelled_WritesWarningSummary_AndDoesNotSetError()
        {
            var profile = await SeedAndLoadProfileAsync();
            var processor = new FakeProcessor(new BackupResult()) { ThrowOnCreate = new OperationCanceledException() };

            var act = () => Handler(processor).HandleAsync(profile, manual: false, CancellationToken.None);

            // Cancellation propagates so the runner can settle the profile back to Idle.
            await act.Should().ThrowAsync<OperationCanceledException>();

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Archive Sync Handler was cancelled after");
            log.Level.Should().Be(OperationLogLevel.Warning);

            // A cancelled run is a warning, not an error — the handler must not flip the profile to Error.
            _statusService.Get(profile.Id).Should().NotBe(ProfileStatus.Error);
        }

        private sealed class FakeProcessor(BackupResult result) : IArchiveSyncProcessor
        {
            public List<string> ItemNames { get; } = [];
            public List<long> RunIndexes { get; } = [];

            public Exception? ThrowOnCreate { get; init; }

            public List<IProgress<double>?> Progresses { get; } = [];

            public Task<BackupResult> CreateArchiveAsync(
                ArchiveSyncItem item, long runIndex, DateTime timestamp, IOperationLogger log, CancellationToken cancellationToken, IProgress<double>? progress = null)
            {
                ItemNames.Add(item.Name);
                RunIndexes.Add(runIndex);
                Progresses.Add(progress);
                if (ThrowOnCreate is not null)
                {
                    throw ThrowOnCreate;
                }
                return Task.FromResult(result);
            }
        }
    }
}
