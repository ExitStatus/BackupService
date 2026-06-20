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
    }
}
