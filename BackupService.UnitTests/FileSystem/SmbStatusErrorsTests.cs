using BackupService.FileSystem;
using BackupService.FileSystem.Smb;
using FluentAssertions;
using SMBLibrary;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class SmbStatusErrorsTests
    {
        [TestCase(NTStatus.STATUS_SHARING_VIOLATION)]
        [TestCase(NTStatus.STATUS_FILE_LOCK_CONFLICT)]
        [TestCase(NTStatus.STATUS_LOCK_NOT_GRANTED)]
        public void AFileLockedOnTheServer_IsTheUsualLockedWarning(NTStatus status)
        {
            // It used to be a plain IOException, counted as an Error on every run.
            var error = SmbStatusErrors.Create(status, $"SMB failed to open 'a.pst' ({status}).");

            FileLock.IsSkippableReadError(error, out var reason).Should().BeTrue();
            reason.Should().Contain("locked");
        }

        [Test]
        public void AnyOtherFailure_IsAnError()
        {
            var error = SmbStatusErrors.Create(NTStatus.STATUS_ACCESS_DENIED, "SMB failed to open 'a.pst'.");

            FileLock.IsSkippableReadError(error, out _).Should().BeFalse();
        }
    }
}
