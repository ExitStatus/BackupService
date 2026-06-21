using BackupService.Security;
using FluentAssertions;

namespace BackupService.UnitTests.Security
{
    [TestFixture]
    public class DpapiSecretProtectorTests
    {
        private readonly DpapiSecretProtector _protector = new();

        [Test]
        public void Protect_ThenUnprotect_RoundTripsThePlaintext()
        {
            const string secret = "S3cr3t-p@ssw0rd!";

            var protectedValue = _protector.Protect(secret);
            var recovered = _protector.Unprotect(protectedValue);

            recovered.Should().Be(secret);
        }

        [Test]
        public void Protect_DoesNotReturnThePlaintext()
        {
            const string secret = "another-password";

            var protectedValue = _protector.Protect(secret);

            protectedValue.Should().NotBe(secret);
            protectedValue.Should().NotContain(secret);
        }

        [Test]
        public void Protect_EmptyString_RoundTrips()
        {
            var protectedValue = _protector.Protect(string.Empty);

            _protector.Unprotect(protectedValue).Should().BeEmpty();
        }
    }
}
