using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Scheduling.ScheduledTasks;
using FluentAssertions;

namespace BackupService.UnitTests.ScheduledTasks
{
    /// <summary>
    /// Runs real processes through <see cref="ProcessRunner"/> (the seam the task runner is otherwise tested against a
    /// fake of) to pin down the shell behaviour: quoting, and when a step counts as finished.
    /// </summary>
    [TestFixture]
    public class ProcessRunnerTests
    {
        private readonly ProcessRunner _sut = new();

        private static ScheduledTaskStep ShellStep(string command) => new()
        {
            Kind = ScheduledTaskStepKind.Command,
            RunViaShell = true,
            Command = command,
        };

        [Test]
        public async Task ShellCommand_WithAQuotedProgramAndAQuotedArgument_RunsAsWritten()
        {
            // Plain "cmd /c" strips the first and last quote of such a command, so it used to fail with
            // "'C:\Program' is not recognized…".
            var command = OperatingSystem.IsWindows()
                ? $"\"{Path.Combine(Environment.SystemDirectory, "where.exe")}\" \"cmd\""
                : "\"/bin/echo\" \"hello world\"";

            var result = await _sut.RunAsync(ShellStep(command), CancellationToken.None);

            result.ExitCode.Should().Be(0, string.Join(Environment.NewLine, result.StandardError));
            result.StandardOutput.Should().Contain(line => line.Contains(OperatingSystem.IsWindows() ? "cmd.exe" : "hello world", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public async Task StepThatStartsABackgroundProgram_FinishesOnlyWhenThatHasFinishedToo()
        {
            // So a launcher that hands its work to a helper can't let the next step start before the work is done.
            var command = OperatingSystem.IsWindows() ? "start /b ping -n 3 127.0.0.1 >nul" : "sleep 2 &";
            var stopwatch = Stopwatch.StartNew();

            var result = await _sut.RunAsync(ShellStep(command), CancellationToken.None);

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            result.ExitCode.Should().Be(0);
        }

        [Test]
        public async Task Stop_EndsWhatTheStepStartedInTheBackground_EvenAfterTheStepsOwnProgramExited()
        {
            // cmd exits at once, leaving ping running with its output redirected away: nothing can reach it through
            // cmd any more. The step's Job Object can.
            if (!OperatingSystem.IsWindows())
            {
                Assert.Ignore("Background programs are tracked through a Windows Job Object.");
                return;
            }
            var startedAt = DateTime.Now.AddSeconds(-1);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            var act = () => _sut.RunAsync(ShellStep("start /b ping -n 60 127.0.0.1 >nul"), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            await Task.Delay(500);
            StartedSince("PING", startedAt).Should().BeEmpty("Stop must end the step's background programs too");
        }

        private static List<int> StartedSince(string processName, DateTime since)
        {
            var found = new List<int>();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.StartTime >= since)
                        {
                            found.Add(process.Id);
                        }
                    }
                    catch (Exception)
                    {
                        // Another user's process, or it exited meanwhile.
                    }
                }
            }
            return found;
        }

        [Test]
        public async Task CapturesStandardOutputErrorAndTheExitCode()
        {
            var command = OperatingSystem.IsWindows()
                ? "echo out-line & echo err-line 1>&2 & exit /b 3"
                : "echo out-line; echo err-line 1>&2; exit 3";

            var result = await _sut.RunAsync(ShellStep(command), CancellationToken.None);

            result.ExitCode.Should().Be(3);
            result.StandardOutput.Should().Contain(line => line.Trim() == "out-line");
            result.StandardError.Should().Contain(line => line.Trim() == "err-line");
        }

        [Test]
        public async Task Cancellation_StopsALongRunningStep()
        {
            var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();

            var act = () => _sut.RunAsync(ShellStep(command), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
    }
}
