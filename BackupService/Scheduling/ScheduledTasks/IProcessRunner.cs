using BackupService.Database;

namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>The outcome of running one <see cref="ScheduledTaskStep"/>.</summary>
    public sealed record ProcessRunResult(
        int ExitCode,
        IReadOnlyList<string> StandardOutput,
        IReadOnlyList<string> StandardError);

    /// <summary>
    /// Runs a single <see cref="ScheduledTaskStep"/> as an OS process and collects its stdout/stderr.
    /// The unit-testable execution seam for <c>ScheduledTaskRunner</c> (the real implementation does the
    /// actual process I/O and is not unit-tested, like <c>BackupFileSystem</c>).
    /// </summary>
    public interface IProcessRunner
    {
        /// <summary>
        /// Starts the step's command, streams stdout/stderr to completion, and returns the exit code and
        /// captured lines. Honours <paramref name="cancellationToken"/> (killing the process tree on cancel).
        /// </summary>
        Task<ProcessRunResult> RunAsync(ScheduledTaskStep step, CancellationToken cancellationToken);
    }
}
