using BackupService.Hosting;
using FluentAssertions;

namespace BackupService.UnitTests.Hosting
{
    [TestFixture]
    public class ApplicationModeTests
    {
        [Test]
        public void Parse_NoArgs_IsForeground()
        {
            var mode = ApplicationModeParser.Parse([], out var isWorker);

            mode.Should().Be(ApplicationMode.Foreground);
            isWorker.Should().BeFalse();
        }

        [Test]
        public void Parse_Stop_IsStop()
        {
            ApplicationModeParser.Parse(["-stop"], out _).Should().Be(ApplicationMode.Stop);
        }

        [TestCase("-background")]
        [TestCase("-bg")]
        public void Parse_BackgroundFlags_AreBackground(string flag)
        {
            ApplicationModeParser.Parse([flag], out _).Should().Be(ApplicationMode.Background);
        }

        [Test]
        public void Parse_Worker_RunsForegroundStyle_ButFlagsWorker()
        {
            var mode = ApplicationModeParser.Parse(["--worker"], out var isWorker);

            mode.Should().Be(ApplicationMode.Foreground);
            isWorker.Should().BeTrue();
        }

        [TestCase("-STOP", ApplicationMode.Stop)]
        [TestCase("-BG", ApplicationMode.Background)]
        [TestCase("-Background", ApplicationMode.Background)]
        public void Parse_IsCaseInsensitive(string flag, ApplicationMode expected)
        {
            ApplicationModeParser.Parse([flag], out _).Should().Be(expected);
        }

        [Test]
        public void Parse_UnknownArgs_FallThroughToForeground()
        {
            ApplicationModeParser.Parse(["--something", "/x"], out var isWorker).Should().Be(ApplicationMode.Foreground);
            isWorker.Should().BeFalse();
        }

        [Test]
        public void Parse_StopTakesPrecedenceOverBackground()
        {
            // Only one mode is returned; -stop wins if both are somehow present.
            ApplicationModeParser.Parse(["-background", "-stop"], out _).Should().Be(ApplicationMode.Stop);
        }
    }
}
