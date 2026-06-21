using BackupService.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace BackupService.UnitTests.Security
{
    [TestFixture]
    public class DataProtectionSecretProtectorTests
    {
        // An in-memory data-protection provider is enough to exercise the wrapper.
        private readonly DataProtectionSecretProtector _protector = new(new EphemeralDataProtectionProvider());

        [Test]
        public void Protect_ThenUnprotect_RoundTripsThePlaintext()
        {
            const string secret = "S3cr3t-p@ssw0rd!";

            var protectedValue = _protector.Protect(secret);

            _protector.Unprotect(protectedValue).Should().Be(secret);
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
