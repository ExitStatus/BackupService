using BackupService.FileSystem.GoogleDrive;
using FluentAssertions;
using Google.Apis.Download;
using Moq;

namespace BackupService.UnitTests.FileSystem
{
    /// <summary>
    /// Covers <see cref="GoogleDriveBackupFileSystem.EnsureCompleteDownload"/> — the check that stops a Drive download
    /// that failed or broke off part-way from being handed back as if it were the whole file.
    /// </summary>
    [TestFixture]
    public class GoogleDriveDownloadCheckTests
    {
        private const string FilePath = @"Photos\a.jpg";

        private static IDownloadProgress Progress(DownloadStatus status, Exception? exception = null)
        {
            var progress = new Mock<IDownloadProgress>();
            progress.SetupGet(p => p.Status).Returns(status);
            progress.SetupGet(p => p.Exception).Returns(exception!);
            return progress.Object;
        }

        private static Func<long> NotCalled() => () => throw new AssertionException("The size should not have been re-read.");

        [Test]
        public void CompletedFullDownload_IsAccepted_WithoutReReadingTheSize()
        {
            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Completed), 100, 100, NotCalled(), FilePath);

            act.Should().NotThrow();
        }

        [Test]
        public void FailedDownload_ThrowsTheDownloadersException()
        {
            // The downloader reports failure through its progress instead of throwing — it must not be ignored.
            var failure = new HttpRequestException("connection reset");

            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Failed, failure), 40, 100, NotCalled(), FilePath);

            act.Should().Throw<HttpRequestException>().Which.Should().BeSameAs(failure);
        }

        [Test]
        public void FailedDownloadWithoutAnException_ThrowsAnIOException()
        {
            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Failed), 40, 100, NotCalled(), FilePath);

            act.Should().Throw<IOException>().WithMessage("*did not complete*");
        }

        [Test]
        public void ShortDownload_WhenTheFileIsStillThatSize_IsIncomplete()
        {
            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Completed), 40, 100, () => 100, FilePath);

            act.Should().Throw<IOException>().WithMessage("*Incomplete download*40 of 100*");
        }

        [Test]
        public void ShortDownload_OfAFileThatShrankSinceItWasListed_IsAccepted()
        {
            // The cached size was stale — re-reading it shows the download got the whole (now smaller) file.
            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Completed), 40, 100, () => 40, FilePath);

            act.Should().NotThrow();
        }

        [TestCase("application/vnd.google-apps.document", "Google Docs")]
        [TestCase("application/vnd.google-apps.spreadsheet", "Google Sheets")]
        [TestCase("application/vnd.google-apps.presentation", "Google Slides")]
        [TestCase("application/vnd.google-apps.shortcut", "Drive shortcut")]
        [TestCase("application/vnd.google-apps.jam", "Google Workspace")] // any other Workspace type
        [TestCase("application/pdf", null)]
        [TestCase("image/jpeg", null)]
        [TestCase(null, null)]
        public void NonDownloadableKind_NamesGoogleWorkspaceTypes_AndIgnoresOrdinaryFiles(string? mimeType, string? kind)
        {
            GoogleDriveBackupFileSystem.NonDownloadableKind(mimeType).Should().Be(kind);
        }

        [Test]
        public void ProtectedReason_IsGivenForGoogleWorkspaceFiles_AndNotForOrdinaryOnes()
        {
            GoogleDriveBackupFileSystem.ProtectedReason("application/vnd.google-apps.spreadsheet")
                .Should().Be("it's a Google Sheets file, which backups never delete or overwrite on Google Drive");
            GoogleDriveBackupFileSystem.ProtectedReason("application/pdf").Should().BeNull();
            GoogleDriveBackupFileSystem.ProtectedReason(null).Should().BeNull();
        }

        [TestCase(0L)]   // no size to check against
        [TestCase(80L)]  // more than reported — not truncation
        public void DownloadWithNothingToCheckOrMoreThanReported_IsAccepted(long expected)
        {
            var act = () => GoogleDriveBackupFileSystem.EnsureCompleteDownload(Progress(DownloadStatus.Completed), 100, expected, NotCalled(), FilePath);

            act.Should().NotThrow();
        }
    }
}
