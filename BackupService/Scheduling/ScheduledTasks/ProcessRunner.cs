using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// Default <see cref="IProcessRunner"/>. Runs a step via <see cref="Process"/> with stdout/stderr
    /// redirected, no shell window, and (optionally) the OS shell. Cross-platform: a shell step runs
    /// through <c>cmd.exe /c</c> on Windows and <c>/bin/sh -c</c> elsewhere. A PowerShell step is written
    /// to a temporary <c>.ps1</c> file and run via <c>pwsh</c> (if available) or <c>powershell</c>.
    /// </summary>
    public sealed class ProcessRunner : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(ScheduledTaskStep step, CancellationToken cancellationToken)
        {
            string? tempScriptPath = null;
            try
            {
                ProcessStartInfo startInfo;
                if (step.Kind == ScheduledTaskStepKind.PowerShell)
                {
                    startInfo = BuildPowerShellStartInfo(step, out tempScriptPath);
                }
                else
                {
                    startInfo = BuildCommandStartInfo(step);
                }

                return await RunProcessAsync(startInfo, cancellationToken);
            }
            finally
            {
                if (tempScriptPath is not null)
                {
                    TryDelete(tempScriptPath);
                }
            }
        }

        private static async Task<ProcessRunResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // Collected on the event threads — use thread-safe queues.
            var stdout = new ConcurrentQueue<string>();
            var stderr = new ConcurrentQueue<string>();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.Enqueue(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.Enqueue(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            // WaitForExitAsync returns when the process exits, but a final flush of the async readers can
            // still be in flight; a parameterless WaitForExit() drains them.
            process.WaitForExit();

            return new ProcessRunResult(process.ExitCode, stdout.ToArray(), stderr.ToArray());
        }

        private static ProcessStartInfo BuildCommandStartInfo(ScheduledTaskStep step)
        {
            var startInfo = NewStartInfo(step.WorkingDirectory);

            if (step.RunViaShell)
            {
                if (OperatingSystem.IsWindows())
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.Arguments = $"/c {step.Command}";
                }
                else
                {
                    startInfo.FileName = "/bin/sh";
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add(step.Command ?? string.Empty);
                }
            }
            else
            {
                startInfo.FileName = step.Command ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(step.Arguments))
                {
                    startInfo.Arguments = step.Arguments;
                }
            }

            return startInfo;
        }

        private static ProcessStartInfo BuildPowerShellStartInfo(ScheduledTaskStep step, out string tempScriptPath)
        {
            // Write the script to a temp .ps1 (UTF-8 with BOM so Windows PowerShell reads non-ASCII correctly).
            tempScriptPath = Path.Combine(Path.GetTempPath(), $"bsvc_task_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(tempScriptPath, step.Script ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var startInfo = NewStartInfo(step.WorkingDirectory);
            startInfo.FileName = ResolvePowerShellExecutable();
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(tempScriptPath);
            return startInfo;
        }

        private static ProcessStartInfo NewStartInfo(string? workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            return startInfo;
        }

        /// <summary>Prefer PowerShell 7+ (<c>pwsh</c>) when it's on PATH; otherwise Windows PowerShell.</summary>
        private static string ResolvePowerShellExecutable() => IsOnPath("pwsh") ? "pwsh" : "powershell";

        private static bool IsOnPath(string executable)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
            {
                return false;
            }

            var names = OperatingSystem.IsWindows()
                ? new[] { executable + ".exe", executable + ".cmd", executable }
                : new[] { executable };

            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                foreach (var name in names)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir, name)))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // A malformed PATH entry — skip it.
                    }
                }
            }

            return false;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort — the process may have exited between the check and the kill.
            }
        }
    }
}
