using BackupService.Components.Pages.Authentication;
using FluentAssertions;

namespace BackupService.UnitTests.Authentication
{
    [TestFixture]
    public class LoginReturnUrlTests
    {
        [TestCase("/")]
        [TestCase("/settings")]
        [TestCase("/?section=logs&log=123")]
        public void ALocalPath_IsFollowed(string url)
        {
            Login.IsLocalPath(url).Should().BeTrue();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("//evil.example")]          // protocol-relative: another site
        [TestCase(@"/\evil.example")]         // browsers treat the backslash as a slash
        [TestCase("https://evil.example/")]
        [TestCase("settings")]                // not rooted
        public void AnythingElse_IsNot(string? url)
        {
            Login.IsLocalPath(url).Should().BeFalse();
        }
    }
}
