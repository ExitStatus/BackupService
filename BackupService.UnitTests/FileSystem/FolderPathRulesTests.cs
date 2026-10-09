using BackupService.FileSystem;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class FolderPathRulesTests
    {
        private static readonly string LocalFullPath = OperatingSystem.IsWindows() ? @"D:\Backups\PC1" : "/srv/backups/pc1";

        [Test]
        public void AFullPath_FitsThisMachine()
        {
            FolderPathRules.Problem(onConnection: false, LocalFullPath).Should().BeNull();
        }

        [TestCase("PC1")]
        [TestCase(@"Backups\PC1")]
        public void ARelativePath_DoesNotFitThisMachine(string folder)
        {
            // Kept after the profile's target was switched from a share to this machine, it resolved against the
            // app's working directory and the backup landed under the app's own folder.
            FolderPathRules.Problem(onConnection: false, folder).Should().NotBeNull();
        }

        [Test]
        public void ABlankFolder_DoesNotFitThisMachine_ButIsAConnectionsRoot()
        {
            FolderPathRules.Problem(onConnection: false, "").Should().NotBeNull();
            FolderPathRules.Problem(onConnection: true, "").Should().BeNull();
        }

        [TestCase("PC1")]
        [TestCase(@"Backups\PC1")]
        [TestCase(@"\Backups\PC1")]
        public void APathRelativeToTheConnectionRoot_FitsAConnection(string folder)
        {
            FolderPathRules.Problem(onConnection: true, folder).Should().BeNull();
        }

        [TestCase(@"D:\Backups")]
        [TestCase(@"\\nas\share\Backups")]
        public void APathOnThisMachine_DoesNotFitAConnection(string folder)
        {
            FolderPathRules.Problem(onConnection: true, folder).Should().NotBeNull();
        }
    }
}
