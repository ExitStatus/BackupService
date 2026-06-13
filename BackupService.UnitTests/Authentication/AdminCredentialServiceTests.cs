using BackupService.Authentication;
using BackupService.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Authentication
{
    [TestFixture]
    public class AdminCredentialServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private Mock<IDatabaseContextFactory> _contextFactory = null!;
        private AdminCredentialService _service = null!;

        [SetUp]
        public void SetUp()
        {
            // A shared in-memory SQLite database, kept alive by holding the
            // connection open for the lifetime of the test. The factory hands out
            // a fresh context over the same connection on each call, matching how
            // the real DatabaseContextFactory is used.
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<BackupDbContext>()
                .UseSqlite(_connection)
                .Options;

            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            _contextFactory = new Mock<IDatabaseContextFactory>();
            _contextFactory
                .Setup(factory => factory.CreateDbContext())
                .Returns(() => new BackupDbContext(_options));

            _service = new AdminCredentialService(
                _contextFactory.Object,
                NullLogger<AdminCredentialService>.Instance);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        [Test]
        public async Task EnsureSeededAsync_WhenNoCredentialExists_CreatesDefaultAdminWithHashedPassword()
        {
            await _service.EnsureSeededAsync();

            await using var context = new BackupDbContext(_options);
            var credential = await context.AdminCredentials.SingleAsync();

            credential.Username.Should().Be("admin");
            credential.PasswordHash.Should().NotBeNullOrEmpty();
            credential.PasswordHash.Should().NotBe("admin",
                "the password must be stored hashed, never in plaintext");
        }

        [Test]
        public async Task EnsureSeededAsync_WhenCredentialAlreadyExists_DoesNotCreateAnother()
        {
            await _service.EnsureSeededAsync();
            await _service.EnsureSeededAsync();

            await using var context = new BackupDbContext(_options);
            (await context.AdminCredentials.CountAsync()).Should().Be(1);
        }

        [Test]
        public async Task VerifyAsync_WithCorrectDefaultCredentials_ReturnsTrue()
        {
            await _service.EnsureSeededAsync();

            var result = await _service.VerifyAsync("admin", "admin");

            result.Should().BeTrue();
        }

        [Test]
        public async Task VerifyAsync_WithWrongPassword_ReturnsFalse()
        {
            await _service.EnsureSeededAsync();

            var result = await _service.VerifyAsync("admin", "not-the-password");

            result.Should().BeFalse();
        }

        [Test]
        public async Task VerifyAsync_WithUnknownUsername_ReturnsFalse()
        {
            await _service.EnsureSeededAsync();

            var result = await _service.VerifyAsync("root", "admin");

            result.Should().BeFalse();
        }

        [Test]
        public async Task VerifyAsync_WhenNoCredentialSeeded_ReturnsFalse()
        {
            var result = await _service.VerifyAsync("admin", "admin");

            result.Should().BeFalse();
        }
    }
}
