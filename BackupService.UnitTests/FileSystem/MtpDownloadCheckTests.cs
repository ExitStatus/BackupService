using BackupService.FileSystem.Mtp;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class MtpDownloadCheckTests
    {
        [TestCase(0UL, 0L)]
        [TestCase(5UL, 5L)]
        [TestCase(0xFFFFFFFFUL, 4294967295L)] // PTP's ">4 GB" cap is a real number, not an overflow
        [TestCase((ulong)long.MaxValue, long.MaxValue)]
        [TestCase(ulong.MaxValue, -1L)]
        public void ToReportedSize_MapsSizesThatDontFitALongToUnknown(ulong length, long expected)
        {
            MtpDownloadCheck.ToReportedSize(length).Should().Be(expected);
        }

        [TestCase(100L, 100L, true)]  // exactly as reported
        [TestCase(100L, 150L, true)]  // more than reported (HEIC->JPEG conversion, a >4 GB file capped at 0xFFFFFFFF)
        [TestCase(0L, 150L, true)]    // device reports 0 for a real file — nothing to verify against
        [TestCase(-1L, 150L, true)]   // reported size didn't fit a long
        [TestCase(100L, 99L, false)]  // short — the transfer ended early
        [TestCase(100L, 0L, false)]
        public void IsComplete_RefusesOnlyAShortDownload(long expected, long actual, bool complete)
        {
            MtpDownloadCheck.IsComplete(expected, actual).Should().Be(complete);
        }

        [Test]
        public void ShouldRetry_AFirstShortDownload()
        {
            MtpDownloadCheck.ShouldRetry(actual: 40, previousActual: null, attempt: 1, maxAttempts: 4).Should().BeTrue();
        }

        [Test]
        public void ShouldRetry_WhenTheShortfallChangesBetweenAttempts()
        {
            MtpDownloadCheck.ShouldRetry(actual: 70, previousActual: 40, attempt: 2, maxAttempts: 4).Should().BeTrue();
        }

        [Test]
        public void ShouldNotRetry_WhenTwoAttemptsInARowAreExactlyAsShort()
        {
            // A dropped session doesn't cut a transfer at the same byte twice, so the device is consistently delivering
            // that much — a third and fourth full transfer wouldn't change anything.
            MtpDownloadCheck.ShouldRetry(actual: 40, previousActual: 40, attempt: 2, maxAttempts: 4).Should().BeFalse();
        }

        [Test]
        public void ShouldNotRetry_OnceTheAttemptsAreUsedUp()
        {
            MtpDownloadCheck.ShouldRetry(actual: 90, previousActual: 70, attempt: 4, maxAttempts: 4).Should().BeFalse();
        }
    }
}
