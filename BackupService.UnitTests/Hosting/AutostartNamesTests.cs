using BackupService.Hosting;
using FluentAssertions;

namespace BackupService.UnitTests.Hosting
{
    [TestFixture]
    public class AutostartNamesTests
    {
        [TestCase("Production")]
        [TestCase("")]
        public void TheInstalledApp_KeepsThePlainNames(string environment)
        {
            AutostartNames.WindowsValueName(environment).Should().Be("BackupService");
            AutostartNames.LinuxDesktopFileName(environment).Should().Be("backupservice.desktop");
        }

        [Test]
        public void ADevelopmentRun_UsesItsOwnEntry_SoItCantRemoveOrRepointTheInstalledOne()
        {
            AutostartNames.WindowsValueName("Development").Should().NotBe(AutostartNames.WindowsValueName("Production"));
            AutostartNames.LinuxDesktopFileName("Development").Should().NotBe(AutostartNames.LinuxDesktopFileName("Production"));
        }
    }
}
