using BackupService.FileSystem;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class FileLockTests
    {
        [Test]
        public void SharingViolation_HResult_IsLockViolation() =>
            FileLock.IsLockViolation(new IOException("x", unchecked((int)0x80070020))).Should().BeTrue();

        [Test]
        public void LockViolation_HResult_IsLockViolation() =>
            FileLock.IsLockViolation(new IOException("x", unchecked((int)0x80070021))).Should().BeTrue();

        [Test]
        public void BeingUsedByAnotherProcess_Message_IsLockViolation() =>
            FileLock.IsLockViolation(
                new IOException("The process cannot access the file because it is being used by another process."))
                .Should().BeTrue();

        [Test]
        public void WrappedInInnerException_IsLockViolation() =>
            FileLock.IsLockViolation(new Exception("wrapper", new IOException("x", unchecked((int)0x80070020))))
                .Should().BeTrue();

        [Test]
        public void OtherIOException_IsNotLockViolation() =>
            FileLock.IsLockViolation(new IOException("disk full")).Should().BeFalse();

        [Test]
        public void Null_IsNotLockViolation() =>
            FileLock.IsLockViolation(null).Should().BeFalse();

        [Test]
        public void CloudFileAccessDenied_HResult_IsCloudFileError() =>
            // 395 = ERROR_CLOUD_FILE_ACCESS_DENIED
            FileLock.IsCloudFileError(new IOException("x", unchecked((int)0x8007018B))).Should().BeTrue();

        [Test]
        public void CloudFile_Message_IsCloudFileError() =>
            FileLock.IsCloudFileError(new IOException("Access to the cloud file is denied.")).Should().BeTrue();

        [Test]
        public void OtherIOException_IsNotCloudFileError() =>
            FileLock.IsCloudFileError(new IOException("disk full")).Should().BeFalse();

        [Test]
        public void LockViolation_IsSkippableReadError_WithLockReason()
        {
            FileLock.IsSkippableReadError(new IOException("x", unchecked((int)0x80070020)), out var reason).Should().BeTrue();
            reason.Should().Contain("locked");
        }

        [Test]
        public void CloudFileError_IsSkippableReadError_WithCloudReason()
        {
            FileLock.IsSkippableReadError(new IOException("x", unchecked((int)0x8007018B)), out var reason).Should().BeTrue();
            reason.Should().Contain("cloud file");
        }

        [Test]
        public void OtherIOException_IsNotSkippableReadError()
        {
            FileLock.IsSkippableReadError(new IOException("disk full"), out var reason).Should().BeFalse();
            reason.Should().BeEmpty();
        }
    }
}
