using BackupService.Hosting;
using FluentAssertions;

namespace BackupService.UnitTests.Hosting
{
    [TestFixture]
    public class LinuxAutostartEntryTests
    {
        [Test]
        public void Build_QuotesExecPath()
        {
            var entry = LinuxAutostartEntry.Build("/opt/backupservice/BackupService", "/opt/backupservice");

            entry.Should().Contain("Exec=\"/opt/backupservice/BackupService\"");
        }

        [Test]
        public void Build_SetsWorkingDirectory()
        {
            var entry = LinuxAutostartEntry.Build("/opt/backupservice/BackupService", "/opt/backupservice");

            entry.Should().Contain("Path=/opt/backupservice");
        }

        [Test]
        public void Build_IsApplicationTypeAndHidden()
        {
            var entry = LinuxAutostartEntry.Build("/opt/backupservice/BackupService", "/opt/backupservice");

            entry.Should().Contain("[Desktop Entry]");
            entry.Should().Contain("Type=Application");
            entry.Should().Contain("Name=Backup Service");
            entry.Should().Contain("NoDisplay=true");
            entry.Should().Contain("Terminal=false");
            entry.Should().Contain("X-GNOME-Autostart-enabled=true");
        }
    }
}
