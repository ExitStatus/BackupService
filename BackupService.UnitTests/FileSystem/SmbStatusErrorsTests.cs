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

        [TestCase(NTStatus.STATUS_USER_SESSION_DELETED)]
        [TestCase(NTStatus.STATUS_NETWORK_NAME_DELETED)]
        public void ALostSession_IsAConnectionLoss(NTStatus status)
        {
            SmbStatusErrors.IsConnectionLost(SmbStatusErrors.Create(status, "x")).Should().BeTrue();
        }

        [Test]
        public void SmbLibrarysDisconnectedMessage_IsAConnectionLoss()
        {
            SmbStatusErrors.IsConnectionLost(new InvalidOperationException("The client is no longer connected")).Should().BeTrue();
        }

        [Test]
        public void AnOrdinaryFailure_IsNotAConnectionLoss()
        {
            SmbStatusErrors.IsConnectionLost(SmbStatusErrors.Create(NTStatus.STATUS_ACCESS_DENIED, "x")).Should().BeFalse();
            SmbStatusErrors.IsConnectionLost(new InvalidOperationException("Something else")).Should().BeFalse();
        }
    }
}
