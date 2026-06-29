using System.Diagnostics;
using BackupService.Database;

namespace BackupService.Hosting
{
    /// <summary>
    /// Cross-process control for the foreground/background run model:
    /// <list type="bullet">
    /// <item>a single-instance guard (a named <see cref="Mutex"/>) so a second launch refuses cleanly instead of
    /// failing to bind Kestrel's port;</item>
    /// <item>a graceful stop signal (a named <see cref="EventWaitHandle"/>) the <c>-stop</c> command sets and the
    /// running host waits on;</item>
    /// <item>a PID file beside the user's data so <c>-stop</c> can report/await the process;</item>
    /// <item><see cref="LaunchDetached"/>, which relaunches this exe as a detached <c>--worker</c> child for
    /// <c>-background</c>.</item>
    /// </list>
    /// The handle names use the per-session <c>Local\</c> namespace, so each interactive Windows user gets an
    /// independent instance — matching the per-user data directory. They are also suffixed with the environment
    /// (Development vs Production) so a developer's debug instance (port 5080, DB in <c>bin\</c>) and the deployed
    /// instance (port 55000, DB in <c>%LOCALAPPDATA%</c>) — which are genuinely independent — can run side by side
    /// instead of the deployed one blocking the debug run.
    /// </summary>
    public static class BackgroundProcessManager
    {
        private static readonly string EnvironmentSuffix = ResolveEnvironmentSuffix();
        private static readonly string InstanceMutexName = $@"Local\BackupService.Instance.{EnvironmentSuffix}";
        private static readonly string StopEventName = $@"Local\BackupService.Stop.{EnvironmentSuffix}";
        private const string PidFileName = "backupservice.pid";

        // The environment determines both the data directory and these handle names, so a Development run and a
        // deployed (Production) run don't share a single-instance lock or stop event.
        private static string ResolveEnvironmentSuffix()
        {
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.IsNullOrWhiteSpace(environment) ? "Production" : environment;
        }

        // Kept alive for the process lifetime so the registered wait and the event aren't collected.
        private static EventWaitHandle? _stopEvent;
        private static RegisteredWaitHandle? _stopRegistration;

        private static string PidFilePath => Path.Combine(BackupDatabaseLocation.GetDataDirectory(), PidFileName);

        /// <summary>
        /// Acquires the single-instance lock. Returns an <see cref="IDisposable"/> to hold for the process lifetime,
        /// or <c>null</c> when another instance already holds it.
        /// </summary>
        public static IDisposable? TryAcquireSingleInstance()
        {
            var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return new InstanceLock(mutex);
        }

        /// <summary>True when another instance currently holds the single-instance lock.</summary>
        public static bool IsAlreadyRunning()
        {
            if (Mutex.TryOpenExisting(InstanceMutexName, out var existing))
            {
                existing.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates the named stop event and wires it so that, when another process sets it (via <c>-stop</c>), the
        /// host shuts down gracefully through <paramref name="lifetime"/>.
        /// </summary>
        public static void RegisterStopSignal(IHostApplicationLifetime lifetime)
        {
            _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName);
            _stopRegistration = ThreadPool.RegisterWaitForSingleObject(
                _stopEvent,
                (_, _) => lifetime.StopApplication(),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        /// <summary>
        /// Signals a running background instance to stop. Returns 0 whether or not one was running (a no-op stop is
        /// not an error). Waits briefly for the process named in the PID file to exit so the caller sees it finish.
        /// </summary>
        public static int RequestStop()
        {
            EventWaitHandle stopEvent;
            try
            {
                stopEvent = EventWaitHandle.OpenExisting(StopEventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Console.WriteLine("Backup Service is not running.");
                return 0;
            }

            using (stopEvent)
            {
                stopEvent.Set();
            }

            Console.WriteLine("Stop requested.");
            WaitForProcessExit();
            return 0;
        }

        /// <summary>
        /// Relaunches this executable as a detached <c>--worker</c> child (the background host) and returns. Refuses if
        /// an instance is already running, or if invoked via <c>dotnet &lt;dll&gt;</c> rather than the published exe.
        /// </summary>
        public static int LaunchDetached()
        {
            if (IsAlreadyRunning())
            {
                Console.Error.WriteLine("Backup Service is already running.");
                return 1;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) ||
                Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Run -background from the published BackupService.exe, not via 'dotnet <dll>'.");
                return 1;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add(ApplicationModeParser.WorkerFlag);

            var process = Process.Start(startInfo);
            if (process is null)
            {
                Console.Error.WriteLine("Failed to start the background process.");
                return 1;
            }

            var logsDirectory = Path.Combine(BackupDatabaseLocation.GetDataDirectory(), "logs");
            Console.WriteLine($"Backup Service started in the background (PID {process.Id}). Logs: {logsDirectory}");
            return 0;
        }

        /// <summary>Records this process's id so <c>-stop</c> can await it. Best effort.</summary>
        public static void WritePid()
        {
            try
            {
                File.WriteAllText(PidFilePath, Environment.ProcessId.ToString());
            }
            catch
            {
                // A missing PID file only weakens -stop's "wait for exit"; the stop signal itself still works.
            }
        }

        /// <summary>Removes the PID file on graceful shutdown. Best effort.</summary>
        public static void DeletePid()
        {
            try
            {
                File.Delete(PidFilePath);
            }
            catch
            {
                // Ignore — a stale PID file is harmless.
            }
        }

        private static void WaitForProcessExit()
        {
            int pid;
            try
            {
                if (!File.Exists(PidFilePath) || !int.TryParse(File.ReadAllText(PidFilePath), out pid))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(15_000))
                {
                    Console.WriteLine("The background process did not exit within 15 seconds.");
                    return;
                }

                Console.WriteLine("Backup Service stopped.");
            }
            catch (ArgumentException)
            {
                // No process with that id — already stopped.
                Console.WriteLine("Backup Service stopped.");
            }
        }

        private sealed class InstanceLock(Mutex mutex) : IDisposable
        {
            public void Dispose()
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Owned on another thread or already released — closing the handle below still frees it.
                }
                finally
                {
                    mutex.Dispose();
                }
            }
        }
    }
}
