using System.Runtime.InteropServices;
using BackupService.FileSystem;
using BackupService.FileSystem.Mtp;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class MtpRetryOutcomeTests
    {
        [Test]
        public void ADeviceThatStillAnswers_FailsJustThisFile()
        {
            var last = new COMException("The object can't be read.");

            var outcome = MtpRetryOutcome.ForExhaustedRetries(last, deviceStillAnswers: true, "CAM1");

            outcome.Should().BeOfType<IOException>("the engines record a per-file IOException and move on to the next file");
            outcome.InnerException.Should().BeSameAs(last);
        }

        [Test]
        public void ADeviceThatHasStoppedAnswering_EndsTheRun()
        {
            var last = new COMException("Not connected.");

            var outcome = MtpRetryOutcome.ForExhaustedRetries(last, deviceStillAnswers: false, "CAM1");

            outcome.Should().BeOfType<EndpointUnavailableException>();
            outcome.InnerException.Should().BeSameAs(last);
        }
    }
}
