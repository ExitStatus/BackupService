using BackupService.Connections;
using BackupService.Connections.Smb;
using BackupService.FileSystem;
using BackupService.FileSystem.Smb;
using FluentAssertions;
using Moq;
using SMBLibrary;
using SMBLibrary.Client;

namespace BackupService.UnitTests.FileSystem
{
    /// <summary>
    /// Covers how the SMB filesystem reads NT status codes — a failure must never be mistaken for "end of file" or
    /// "doesn't exist", either of which a sync engine would act on (a truncated copy, or a file read as deleted).
    /// </summary>
    [TestFixture]
    public class SmbBackupFileSystemTests
    {
        private static Mock<ISMBFileStore> StoreReturning(NTStatus createStatus)
        {
            var store = new Mock<ISMBFileStore>();
            object handle = new();
            FileStatus fileStatus;
            store.Setup(s => s.CreateFile(out handle, out fileStatus, It.IsAny<string>(), It.IsAny<AccessMask>(),
                    It.IsAny<SMBLibrary.FileAttributes>(), It.IsAny<ShareAccess>(), It.IsAny<CreateDisposition>(),
                    It.IsAny<CreateOptions>(), It.IsAny<SecurityContext>()))
                .Returns(createStatus);
            return store;
        }

        private static SmbBackupFileSystem FileSystemOver(ISMBFileStore store) => new(new SMB2Client(), store);

        [Test]
        public void FileExists_IsTrueWhenItOpens()
        {
            FileSystemOver(StoreReturning(NTStatus.STATUS_SUCCESS).Object).FileExists(@"Backups\a.txt").Should().BeTrue();
        }

        [TestCase(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND)]
        [TestCase(NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)]
        [TestCase(NTStatus.STATUS_NO_SUCH_FILE)]
        [TestCase(NTStatus.STATUS_FILE_IS_A_DIRECTORY)] // a folder of that name isn't a file
        public void FileExists_IsFalseOnlyForNotFound(NTStatus status)
        {
            FileSystemOver(StoreReturning(status).Object).FileExists(@"Backups\a.txt").Should().BeFalse();
        }

        [TestCase(NTStatus.STATUS_ACCESS_DENIED)]
        [TestCase(NTStatus.STATUS_SHARING_VIOLATION)]
        [TestCase(NTStatus.STATUS_INVALID_SMB)] // what SMBLibrary reports on a timeout
        public void FileExists_ThrowsWhenTheStatusSaysNothingAboutExistence(NTStatus status)
        {
            // Reporting "doesn't exist" here would let a two-way sync read a live (e.g. locked) file as deleted.
            var act = () => FileSystemOver(StoreReturning(status).Object).FileExists(@"Backups\a.pst");

            act.Should().Throw<IOException>().WithMessage($"*{status}*");
        }

        [Test]
        public void DirectoryExists_ThrowsOnAccessDenied_RatherThanReportingTheFolderMissing()
        {
            var act = () => FileSystemOver(StoreReturning(NTStatus.STATUS_ACCESS_DENIED).Object).DirectoryExists("Backups");

            act.Should().Throw<IOException>();
        }

        [Test]
        public void ReadingAfterAFailedRead_Throws_InsteadOfEndingTheStreamEarly()
        {
            // SMBLibrary leaves the data null on any failed read; that used to look like a clean end-of-file.
            var store = StoreReturning(NTStatus.STATUS_SUCCESS);
            byte[]? data = null;
            store.Setup(s => s.ReadFile(out data, It.IsAny<object>(), It.IsAny<long>(), It.IsAny<int>()))
                .Returns(NTStatus.STATUS_INVALID_SMB);
            using var stream = FileSystemOver(store.Object).OpenRead(@"Backups\a.txt");

            var act = () => stream.ReadByte();

            act.Should().Throw<IOException>().WithMessage("*STATUS_INVALID_SMB*");
        }

        [Test]
        public void ReadingAtEndOfFile_EndsTheStream()
        {
            var store = StoreReturning(NTStatus.STATUS_SUCCESS);
            byte[]? data = null;
            store.Setup(s => s.ReadFile(out data, It.IsAny<object>(), It.IsAny<long>(), It.IsAny<int>()))
                .Returns(NTStatus.STATUS_END_OF_FILE);
            using var stream = FileSystemOver(store.Object).OpenRead(@"Backups\a.txt");

            stream.ReadByte().Should().Be(-1);
        }

        // ---- reconnect after the session drops ----

        private static readonly SmbConnectionInfo Info = new("nas", 445, "files", null, "user", "pw", null);

        private static Mock<ISMBFileStore> StoreThrowingDisconnected()
        {
            var store = new Mock<ISMBFileStore>();
            object handle = new();
            FileStatus fileStatus;
            store.Setup(s => s.CreateFile(out handle, out fileStatus, It.IsAny<string>(), It.IsAny<AccessMask>(),
                    It.IsAny<SMBLibrary.FileAttributes>(), It.IsAny<ShareAccess>(), It.IsAny<CreateDisposition>(),
                    It.IsAny<CreateOptions>(), It.IsAny<SecurityContext>()))
                .Throws(new InvalidOperationException("The client is no longer connected"));
            return store;
        }

        private static SmbBackupFileSystem Reconnecting(
            ISMBFileStore store, Func<SmbConnectionInfo, (SMB2Client, ISMBFileStore)> open, List<TimeSpan>? waits = null) =>
            new(new SMB2Client(), store, Info, open, d => waits?.Add(d), isConnected: _ => true);

        [Test]
        public void AfterTheConnectionDrops_ItReconnects_AndCarriesOn()
        {
            // The 00:01:30 run: one dropped session used to fail every remaining folder of the run.
            var fresh = StoreReturning(NTStatus.STATUS_SUCCESS);
            var opens = 0;
            var waits = new List<TimeSpan>();
            var fs = Reconnecting(StoreThrowingDisconnected().Object, _ => { opens++; return (new SMB2Client(), fresh.Object); }, waits);

            fs.DirectoryExists("Backups").Should().BeTrue();
            fs.DirectoryExists("Backups").Should().BeTrue();

            opens.Should().Be(1, "the second call runs on the new session");
            waits.Should().HaveCount(1);
        }

        [TestCase(NTStatus.STATUS_USER_SESSION_DELETED)]
        [TestCase(NTStatus.STATUS_NETWORK_NAME_DELETED)]
        public void AnExpiredSession_AlsoReconnects(NTStatus status)
        {
            var opens = 0;
            var fs = Reconnecting(StoreReturning(status).Object, _ => { opens++; return (new SMB2Client(), StoreReturning(NTStatus.STATUS_SUCCESS).Object); });

            fs.FileExists(@"Backups\a.txt").Should().BeTrue();
            opens.Should().Be(1);
        }

        [Test]
        public void AnOrdinaryFailure_DoesNotReconnect()
        {
            var opens = 0;
            var fs = Reconnecting(StoreReturning(NTStatus.STATUS_ACCESS_DENIED).Object, _ => { opens++; return (new SMB2Client(), StoreReturning(NTStatus.STATUS_SUCCESS).Object); });

            var act = () => fs.DirectoryExists("Backups");

            act.Should().Throw<IOException>();
            opens.Should().Be(0);
        }

        [Test]
        public void WhenTheServerCantBeReached_TheRunEndsWithOneEndpointUnavailable()
        {
            var attempts = 0;
            var fs = Reconnecting(StoreThrowingDisconnected().Object, _ => { attempts++; throw new SmbBrowseException("Could not connect to nas."); });

            var act = () => fs.CreateDirectory(@"Backups\a");

            act.Should().Throw<EndpointUnavailableException>().WithMessage("*nas*could not be re-established*");
            attempts.Should().Be(3);
        }

        [Test]
        public void WhenTheNewSessionDropsToo_TheRunEndsWithEndpointUnavailable()
        {
            var fs = Reconnecting(StoreThrowingDisconnected().Object, _ => (new SMB2Client(), StoreThrowingDisconnected().Object));

            var act = () => fs.GetFiles("Backups");

            act.Should().Throw<EndpointUnavailableException>().WithMessage("*keeps dropping*");
        }
    }
}
