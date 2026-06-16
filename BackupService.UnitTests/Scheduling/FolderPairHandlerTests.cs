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

        [Test]
        public async Task HandleAsync_WritesSingleLogWithSuccessSummaryAndDetails()
        {
            int profileId;
            using (var db = new BackupDbContext(_options))
            {
                var profile = new Profile
                {
                    Name = "Docs",
                    Type = ProfileType.FolderPair,
                    DateCreated = DateTimeOffset.UtcNow,
                    FolderPairs = { new FolderPair { Name = "P", SourceFolder = @"C:\a", TargetFolder = @"D:\b" } },
                };
                db.Profiles.Add(profile);
                db.SaveChanges();
                profileId = profile.Id;
            }

            Profile loaded;
            await using (var db = new BackupDbContext(_options))
            {
                loaded = await db.Profiles.Include(p => p.FolderPairs).SingleAsync();
            }

            var handler = new FolderPairHandler(new OperationLogFactory(_dbFactory), _statusService, NullLogger<FolderPairHandler>.Instance);

            await handler.HandleAsync(loaded, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);

            // Exactly one log event per run, with the summary message + Info level.
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Folder Pairs Handler ran successfully in");
            log.ProfileId.Should().Be(profileId);
            log.Level.Should().Be(OperationLogLevel.Info);

            // The folder-pair details were written under that same log.
            var details = await verify.OperationLogDetails.Where(d => d.OperationLogId == log.Id).ToListAsync();
            details.Should().ContainSingle(d => d.Message == @"P: C:\a -> D:\b");

            _statusService.Get(profileId).Should().Be(ProfileStatus.Idle);
        }

        [Test]
        public async Task HandleAsync_OnException_SetsErrorSummaryAndStatusAndRethrows()
        {
            // Make a detail write throw so the handler's try-body fails after the log is created.
            var loggerMock = new Mock<IOperationLogger>();
            loggerMock.Setup(l => l.AppendAsync(It.IsAny<string[]>())).ThrowsAsync(new InvalidOperationException("boom"));

            var factoryMock = new Mock<IOperationLogFactory>();
            factoryMock
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(loggerMock.Object);

            var profile = new Profile
            {
                Id = 5,
                Name = "Docs",
                Type = ProfileType.FolderPair,
                FolderPairs = { new FolderPair { Name = "P", SourceFolder = @"C:\a", TargetFolder = @"D:\b" } },
            };
            var handler = new FolderPairHandler(factoryMock.Object, _statusService, NullLogger<FolderPairHandler>.Instance);

            var act = () => handler.HandleAsync(profile, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
            _statusService.Get(5).Should().Be(ProfileStatus.Error);

            // The same single log's header is rewritten to the failure summary at Error level.
            loggerMock.Verify(
                l => l.SetSummaryAsync(
                    It.Is<string>(s => s.StartsWith("Folder Pairs Handler failed in")),
                    OperationLogLevel.Error),
                Times.Once);
        }
    }
}
