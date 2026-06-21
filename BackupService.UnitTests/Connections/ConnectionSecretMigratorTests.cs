using BackupService.Connections;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Connections
{
    [TestFixture]
    public class ConnectionSecretMigratorTests
    {
        private const string LegacyPrefix = "dpapi:";

        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private ConnectionSecretMigrator _migrator = null!;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<BackupDbContext>().UseSqlite(_connection).Options;
            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            var factoryMock = new Mock<IDatabaseContextFactory>();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            _dbFactory = factoryMock.Object;

            // "New" format = ReversibleProtector ("enc:base64"); "legacy" format = a "dpapi:" prefix the
            // fake reader understands. (The real reader is DPAPI; not exercised here.)
            _migrator = new ConnectionSecretMigrator(_dbFactory, new ReversibleProtector(), new FakeLegacyReader(), NullLogger<ConnectionSecretMigrator>.Instance);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private int SeedConnection(string passwordEncrypted)
        {
            using var db = new BackupDbContext(_options);
            var connection = new Connection
            {
                Name = "NAS",
                Type = ConnectionType.Smb,
                DateCreated = DateTimeOffset.UtcNow,
                Smb = new SmbConnectionSettings
                {
                    Host = "h", ShareName = "s", Username = "u",
                    PasswordEncrypted = passwordEncrypted,
                },
            };
            db.Connections.Add(connection);
            db.SaveChanges();
            return connection.Id;
        }

        [Test]
        public void Migrate_ReEncryptsLegacyPassword_IntoTheCurrentFormat()
        {
            var id = SeedConnection(LegacyPrefix + "hunter2");

            _migrator.Migrate();

            using var db = new BackupDbContext(_options);
            var stored = db.SmbConnectionSettings.Single(s => s.ConnectionId == id).PasswordEncrypted;
            new ReversibleProtector().Unprotect(stored).Should().Be("hunter2"); // now decryptable by the current protector
        }

        [Test]
        public void Migrate_LeavesAnAlreadyCurrentPassword_Unchanged()
        {
            var current = new ReversibleProtector().Protect("already-new");
            var id = SeedConnection(current);

            _migrator.Migrate();

            using var db = new BackupDbContext(_options);
            db.SmbConnectionSettings.Single(s => s.ConnectionId == id).PasswordEncrypted.Should().Be(current);
        }

        [Test]
        public void Migrate_LeavesAnUnreadablePassword_Unchanged()
        {
            var id = SeedConnection("garbage-neither-format");

            _migrator.Migrate();

            using var db = new BackupDbContext(_options);
            db.SmbConnectionSettings.Single(s => s.ConnectionId == id).PasswordEncrypted.Should().Be("garbage-neither-format");
        }

        // Stand-in for the real DPAPI reader: "reads" values with a known prefix.
        private sealed class FakeLegacyReader : ILegacySecretReader
        {
            public bool TryRead(string storedValue, out string plaintext)
            {
                if (storedValue.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                {
                    plaintext = storedValue[LegacyPrefix.Length..];
                    return true;
                }
                plaintext = string.Empty;
                return false;
            }
        }
    }
}
