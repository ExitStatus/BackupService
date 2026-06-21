using BackupService.Connections;
using BackupService.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Connections
{
    [TestFixture]
    public class ConnectionResolverTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private ConnectionResolver _resolver = null!;

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

            _resolver = new ConnectionResolver(factoryMock.Object, new ReversibleProtector());
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task GetSmbInfoAsync_DecryptsThePassword_AndMapsFields()
        {
            int id;
            await using (var db = new BackupDbContext(_options))
            {
                var connection = new Connection
                {
                    Name = "NAS",
                    DateCreated = DateTimeOffset.UtcNow,
                    Smb = new SmbConnectionSettings
                    {
                        Host = "host",
                        Port = 4450,
                        ShareName = "Share",
                        Domain = "DOM",
                        Username = "u",
                        PasswordEncrypted = new ReversibleProtector().Protect("p@ss"),
                        RootFolder = "root/sub",
                    },
                };
                db.Connections.Add(connection);
                await db.SaveChangesAsync();
                id = connection.Id;
            }

            var info = await _resolver.GetSmbInfoAsync(id);

            info.Host.Should().Be("host");
            info.Port.Should().Be(4450);
            info.Share.Should().Be("Share");
            info.Domain.Should().Be("DOM");
            info.Username.Should().Be("u");
            info.Password.Should().Be("p@ss");
            info.RootFolder.Should().Be("root/sub");
        }
    }
}
