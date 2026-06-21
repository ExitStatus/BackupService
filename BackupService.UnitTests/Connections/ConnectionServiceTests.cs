using BackupService.Connections;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Connections
{
    [TestFixture]
    public class ConnectionServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private ConnectionService _service = null!;

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

            var logger = new Mock<IOperationLogger>();
            var logFactory = new Mock<IOperationLogFactory>();
            logFactory
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(logger.Object);

            _service = new ConnectionService(_dbFactory, logFactory.Object, new ReversibleProtector());
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private static SmbConnectionInput Smb(string password = "secret") =>
            new("fileserver", 445, "Backups", "WORKGROUP", "user", password, "sub/folder");

        [Test]
        public async Task CreateAsync_PersistsConnection_WithEncryptedPassword()
        {
            var id = await _service.CreateAsync("NAS", ConnectionType.Smb, Smb("plain-pwd"));

            await using var db = new BackupDbContext(_options);
            var settings = await db.SmbConnectionSettings.SingleAsync();

            settings.ConnectionId.Should().Be(id);
            settings.Host.Should().Be("fileserver");
            settings.ShareName.Should().Be("Backups");
            settings.Username.Should().Be("user");
            settings.RootFolder.Should().Be("sub/folder");
            settings.PasswordEncrypted.Should().NotBe("plain-pwd");
            new ReversibleProtector().Unprotect(settings.PasswordEncrypted).Should().Be("plain-pwd");
        }

        [Test]
        public async Task UpdateAsync_WithBlankPassword_KeepsStoredSecret()
        {
            var id = await _service.CreateAsync("NAS", ConnectionType.Smb, Smb("original"));

            // Edit other fields but leave the password blank.
            await _service.UpdateAsync(id, "NAS-renamed", Smb(password: null!) with { Host = "newhost" });

            await using var db = new BackupDbContext(_options);
            var connection = await db.Connections.Include(c => c.Smb).SingleAsync();

            connection.Name.Should().Be("NAS-renamed");
            connection.Smb!.Host.Should().Be("newhost");
            new ReversibleProtector().Unprotect(connection.Smb.PasswordEncrypted).Should().Be("original");
        }

        [Test]
        public async Task UpdateAsync_WithNewPassword_ReEncrypts()
        {
            var id = await _service.CreateAsync("NAS", ConnectionType.Smb, Smb("original"));

            await _service.UpdateAsync(id, "NAS", Smb("changed"));

            await using var db = new BackupDbContext(_options);
            var settings = await db.SmbConnectionSettings.SingleAsync();
            new ReversibleProtector().Unprotect(settings.PasswordEncrypted).Should().Be("changed");
        }

        [Test]
        public async Task DeleteAsync_RemovesConnection_WhenNotInUse()
        {
            var id = await _service.CreateAsync("NAS", ConnectionType.Smb, Smb());

            var result = await _service.DeleteAsync(id);

            result.Deleted.Should().BeTrue();
            await using var db = new BackupDbContext(_options);
            (await db.Connections.CountAsync()).Should().Be(0);
        }

        [Test]
        public async Task DeleteAsync_IsBlocked_WhenReferencedByAFolderPair()
        {
            var id = await _service.CreateAsync("NAS", ConnectionType.Smb, Smb());
            await SeedFolderPairUsingConnectionAsync(id);

            var result = await _service.DeleteAsync(id);

            result.Deleted.Should().BeFalse();
            result.Error.Should().Contain("in use");
            await using var db = new BackupDbContext(_options);
            (await db.Connections.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task GetSummariesAsync_ReturnsNameAndType_NameOrdered()
        {
            await _service.CreateAsync("Zeta", ConnectionType.Smb, Smb());
            await _service.CreateAsync("Alpha", ConnectionType.Smb, Smb());

            var summaries = await _service.GetSummariesAsync();

            summaries.Select(s => s.Name).Should().ContainInOrder("Alpha", "Zeta");
            summaries.Should().OnlyContain(s => s.Type == ConnectionType.Smb);
        }

        private async Task SeedFolderPairUsingConnectionAsync(int connectionId)
        {
            await using var db = new BackupDbContext(_options);
            var profile = new Profile { Name = "P", Type = ProfileType.FolderPair, DateCreated = DateTimeOffset.UtcNow };
            profile.FolderPairs.Add(new FolderPair
            {
                Name = "pair",
                SourceFolder = "in",
                TargetFolder = "C:\\out",
                SourceConnectionId = connectionId,
            });
            db.Profiles.Add(profile);
            await db.SaveChangesAsync();
        }
    }
}
