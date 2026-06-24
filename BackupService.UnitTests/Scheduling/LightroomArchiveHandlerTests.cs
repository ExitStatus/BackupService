using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
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
    public class LightroomArchiveHandlerTests
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
                    Name = "LR",
                    Type = ProfileType.LightroomArchive,
                    LightroomFolder = @"C:\lr",
                    RawFormats = ".DNG,.ARW",
                    RawFolderName = "RAW",
                    DateCreated = DateTimeOffset.UtcNow,
                    LightroomArchiveItems = { new LightroomArchiveItem { Name = "L", SourceFolder = @"C:\a", TargetFolder = @"D:\b", DebounceMilliseconds = 1000 } },
                });
                db.SaveChanges();
            }

            await using var load = new BackupDbContext(_options);
            return await load.Profiles.Include(p => p.LightroomArchiveItems).SingleAsync();
        }

        private LightroomArchiveHandler Handler(ILightroomArchiveProcessor processor) =>
            new(new OperationLogFactory(_dbFactory), processor, Mock.Of<IBackupFileSystem>(), _statusService, Mock.Of<IBackupRunRecorder>(), NullLogger<LightroomArchiveHandler>.Instance);

        [Test]
        public async Task HandleAsync_RunsEachItem_WritesSummaryWithCounts()
        {
            var profile = await SeedAndLoadProfileAsync();
            var processor = new FakeProcessor(new BackupResult { Copied = 2 });

            await Handler(processor).HandleAsync(profile, manual: false, CancellationToken.None);

            processor.ItemNames.Should().Equal("L");

            await using var verify = new BackupDbContext(_options);

            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Lightroom Archive Handler ran successfully in");
            log.Name.Should().Contain("2 copied");
            log.Level.Should().Be(OperationLogLevel.Info);
            log.ProfileId.Should().Be(profile.Id);

            var details = await verify.OperationLogDetails.Where(d => d.OperationLogId == log.Id).ToListAsync();
            details.Should().ContainSingle(d => d.Message == @"Lightroom archive 'L': C:\a -> D:\b");
        }

        [Test]
        public async Task HandleAsync_WhenManual_PrefixesLogWithManualTag()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeProcessor(new BackupResult())).HandleAsync(profile, manual: true, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("[Manual] Lightroom Archive Handler ran successfully in");
        }

        [Test]
        public async Task HandleAsync_WhenProcessorReportsErrors_SummaryReflectsFailure()
        {
            var profile = await SeedAndLoadProfileAsync();

            await Handler(new FakeProcessor(new BackupResult { Copied = 1, Errors = 2 }))
                .HandleAsync(profile, manual: false, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Lightroom Archive Handler completed with 2 error(s) in");
            log.Level.Should().Be(OperationLogLevel.Error);
        }

        private sealed class FakeProcessor(BackupResult result) : ILightroomArchiveProcessor
        {
            public List<string> ItemNames { get; } = [];

            public Task<BackupResult> ProcessBatchAsync(
                LightroomArchiveItem item, LightroomArchiveSettings settings, IReadOnlyCollection<string> changedPaths,
                IReadOnlyCollection<string> deletedPaths, IOperationLogger log, IProgress<int>? progress, CancellationToken cancellationToken)
            {
                ItemNames.Add(item.Name);
                return Task.FromResult(result);
            }
        }
    }
}
