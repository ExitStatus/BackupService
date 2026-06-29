using System.Collections.Concurrent;
using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Notifications;
using BackupService.ScheduledTasks;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// Default <see cref="IScheduledTaskRunner"/>. Loads the task, brackets the run with status and
    /// operation-log bookkeeping, runs each step via <see cref="IProcessRunner"/> (stopping at the first
    /// non-zero exit, which marks the run failed), captures stdout/stderr into the log detail, and records
    /// one <see cref="BackupRun"/> row. Mirrors <c>BackupRunner</c> + a profile handler in one (there is
    /// only one kind of scheduled-task work).
    /// </summary>
    public sealed class ScheduledTaskRunner(
        IDatabaseContextFactory contextFactory,
        IOperationLogFactory operationLogFactory,
        IScheduledTaskStatusService statusService,
        IProcessRunner processRunner,
        IBackupRunRecorder runRecorder,
        ILogger<ScheduledTaskRunner> logger,
        IDesktopNotifier? notifier = null) : IScheduledTaskRunner
    {
        // The cancellation source for each in-progress run, keyed by task id, so the UI's Stop button
        // (RequestStop) can cancel a run that's already under way.
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

        public async Task RunAsync(int taskId, bool manual = false, CancellationToken cancellationToken = default)
        {
            ScheduledTask? task;
            await using (var db = contextFactory.CreateDbContext())
            {
                task = await db.ScheduledTasks
                    .Include(t => t.Steps)
                    .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
            }

            if (task is null)
            {
                logger.LogWarning("Scheduled-task run skipped: task {TaskId} no longer exists.", taskId);
                return;
            }

            // Don't run while an admin has the task open in an edit/delete dialog.
            if (statusService.IsLocked(taskId))
            {
                logger.LogInformation("Scheduled-task run skipped: task {TaskId} is open in an edit/delete dialog.", taskId);
                return;
            }

            // Only one run per task at a time: bail out if one is already in progress.
            if (!statusService.TryBeginRun(taskId))
            {
                logger.LogInformation("Scheduled-task run skipped for task {TaskId}: a run is already in progress.", taskId);
                return;
            }

            var finalStatus = ProfileStatus.Idle;

            // A linked source so a run can be stopped either by the host shutting down (the passed token)
            // or by the user's Stop button (RequestStop cancels this source).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[taskId] = cts;
            try
            {
                var outcome = await ExecuteAsync(task, manual, cts.Token);
                finalStatus = outcome == RunOutcome.Failed ? ProfileStatus.Error : ProfileStatus.Idle;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Stopped on request (Stop button or shutdown): not a failure. The run already finished its
                // log as a warning; return the task to Idle so it waits for its next scheduled run.
                finalStatus = ProfileStatus.Idle;
                logger.LogInformation("Scheduled task {TaskId} ({TaskName}) was cancelled.", task.Id, task.Name);
            }
            catch (Exception ex)
            {
                finalStatus = ProfileStatus.Error;
                logger.LogError(ex, "Scheduled task failed for task {TaskId} ({TaskName}).", task.Id, task.Name);
            }
            finally
            {
                _running.TryRemove(taskId, out _);
            }

            // Persist DateLastRun BEFORE flipping the status (the grid reloads on the status change, so the
            // timestamp must already be saved), and never leave the status stuck on Running.
            try
            {
                await StampLastRunAsync(task.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to record last-run time for task {TaskId}.", task.Id);
            }

            statusService.Set(task.Id, finalStatus);
        }

        public bool RequestStop(int taskId)
        {
            if (!_running.TryGetValue(taskId, out var cts))
            {
                return false;
            }

            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the lookup and the cancel — nothing to stop.
                return false;
            }
        }

        /// <summary>
        /// Runs the task's steps in order, owning the single operation log + the BackupRun row (written in a
        /// <c>finally</c> so they exist even on a catastrophic failure). Returns the run's outcome; a
        /// cancellation or unexpected exception propagates (the caller maps it to a status).
        /// </summary>
        private async Task<RunOutcome> ExecuteAsync(ScheduledTask task, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var prefix = manual ? "[Manual] " : string.Empty;
            var name = $"{prefix}Scheduled Task '{task.Name}'";

            var steps = task.Steps.OrderBy(s => s.Order).ToList();
            var outcome = RunOutcome.Success;
            var fatal = false;
            var cancelled = false;
            var failedStep = false;
            string? failedLabel = null;
            var stepsRun = 0;

            var log = await operationLogFactory.CreateAsync(
                $"{name} running with {steps.Count} step(s).",
                cancellationToken: cancellationToken);

            try
            {
                if (steps.Count == 0)
                {
                    await log.AppendAsync("No steps configured.");
                }
                else
                {
                    for (var i = 0; i < steps.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var step = steps[i];
                        var label = StepLabel(step, i);
                        await log.AppendAsync($"Running {label}: {CommandText(step)}");

                        var result = await processRunner.RunAsync(step, cancellationToken);
                        stepsRun++;

                        if (result.StandardOutput.Count > 0)
                        {
                            await log.AppendAsync(result.StandardOutput.Select(line => $"[{label}] {line}").ToArray());
                        }
                        if (result.StandardError.Count > 0)
                        {
                            await log.AppendAsync(OperationLogLevel.Error, result.StandardError.Select(line => $"[{label}] {line}").ToArray());
                        }

                        if (result.ExitCode != 0)
                        {
                            failedStep = true;
                            failedLabel = label;
                            await log.AppendAsync(OperationLogLevel.Error, $"{label} exited with code {result.ExitCode} — stopping the run.");
                            break;
                        }

                        await log.AppendAsync($"{label} completed (exit code 0).");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped on request (the Stop button or shutdown). Not a failure — log it as a warning.
                cancelled = true;
                await log.AppendAsync(OperationLogLevel.Warning, "Run cancelled — stopped before completion.");
                throw;
            }
            catch (Exception ex)
            {
                // Catastrophic failure (e.g. the executable couldn't be started).
                fatal = true;
                logger.LogError(ex, "Scheduled task run failed for task {TaskId} ({TaskName}).", task.Id, task.Name);
                await log.ErrorAsync("Scheduled task failed", ex);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);
                outcome = cancelled ? RunOutcome.CompletedWithWarnings
                    : fatal || failedStep ? RunOutcome.Failed
                    : RunOutcome.Success;

                // Record the structured run row before the summary write (whose ILogWatcher.Notify the
                // dashboard refreshes on) so the new row is visible when the dashboard reloads.
                await runRecorder.RecordScheduledTaskAsync(
                    task.Id, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds, outcome, log.OperationLogId, CancellationToken.None);

                var ran = $"{stepsRun} of {steps.Count} step(s) ran";
                var (summary, level) = cancelled
                    ? ($"{name} was cancelled after {duration} — {ran}", OperationLogLevel.Warning)
                    : fatal
                        ? ($"{name} failed in {duration} — {ran}", OperationLogLevel.Error)
                        : failedStep
                            ? ($"{name} failed at {failedLabel} in {duration} — {ran}", OperationLogLevel.Error)
                            : ($"{name} ran successfully in {duration} — {steps.Count} step(s)", OperationLogLevel.Info);
                await log.SetSummaryAsync(summary, level);

                // Desktop notification for the completed run (a no-op unless on Windows with notifications on).
                // A cancelled run isn't a completion, so it's skipped.
                if (!cancelled)
                {
                    notifier?.NotifyTaskCompleted(task.Name, outcome);
                }
            }

            return outcome;
        }

        private async Task StampLastRunAsync(int taskId, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
            if (task is null)
            {
                return;
            }

            task.DateLastRun = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        private static string StepLabel(ScheduledTaskStep step, int index) =>
            string.IsNullOrWhiteSpace(step.Name) ? $"step {index + 1}" : $"step {index + 1} '{step.Name}'";

        private static string CommandText(ScheduledTaskStep step) =>
            step.Kind == ScheduledTaskStepKind.PowerShell
                ? "PowerShell script"
                : step.RunViaShell
                    ? $"shell: {step.Command}"
                    : string.IsNullOrWhiteSpace(step.Arguments) ? step.Command ?? string.Empty : $"{step.Command} {step.Arguments}";

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
