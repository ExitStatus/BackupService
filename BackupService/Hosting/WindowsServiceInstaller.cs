using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace BackupService.Hosting
{
    /// <summary>
    /// Installs/uninstalls this executable as a Windows Service via <c>sc.exe</c>.
    /// Invoked from the command line (<c>--install</c> / <c>--uninstall</c>); requires
    /// an elevated (Administrator) prompt.
    /// </summary>
    public static class WindowsServiceInstaller
    {
        public const string ServiceName = "BackupService";
        public const string DisplayName = "Backup Service";

        public static int Install()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Service install is only supported on Windows.");
                return 1;
            }

            if (!IsElevated())
            {
                Console.Error.WriteLine("Administrator privileges required. Re-run --install from an elevated prompt.");
                return 1;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) ||
                Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Run --install from the published BackupService.exe, not via 'dotnet <dll>'.");
                return 1;
            }

            // Replace any existing service.
            if (ServiceExists())
            {
                Console.WriteLine($"Service '{ServiceName}' already exists - replacing it.");
                RunSc("stop", ServiceName);
                RunSc("delete", ServiceName);
                WaitForServiceRemoved();
            }

            // sc.exe quirk: each "key=" and its value are separate tokens (== "key= value").
            var create = RunSc(
                "create", ServiceName,
                "binPath=", exePath,
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", DisplayName);

            // 1072 = "marked for deletion": the old service hasn't fully gone yet; retry once.
            if (create.ExitCode == 1072)
            {
                WaitForServiceRemoved();
                create = RunSc(
                    "create", ServiceName,
                    "binPath=", exePath,
                    "start=", "auto",
                    "obj=", "LocalSystem",
                    "DisplayName=", DisplayName);
            }

            if (create.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to create service (sc exit code {create.ExitCode}).");
                return 1;
            }

            RunSc("description", ServiceName, "Backup Service background worker and admin UI.");

            var start = RunSc("start", ServiceName);
            if (start.ExitCode != 0)
            {
                Console.Error.WriteLine($"Service '{ServiceName}' was installed but failed to start (sc exit code {start.ExitCode}).");
                return 1;
            }

            Console.WriteLine($"Service '{ServiceName}' installed (autostart, LocalSystem) and started.");
            return 0;
        }

        public static int Uninstall()
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Service uninstall is only supported on Windows.");
                return 1;
            }

            if (!IsElevated())
            {
                Console.Error.WriteLine("Administrator privileges required. Re-run --uninstall from an elevated prompt.");
                return 1;
            }

            if (!ServiceExists())
            {
                Console.WriteLine($"Service '{ServiceName}' is not installed.");
                return 0;
            }

            RunSc("stop", ServiceName);

            var delete = RunSc("delete", ServiceName);
            if (delete.ExitCode != 0)
            {
                Console.Error.WriteLine($"Failed to delete service (sc exit code {delete.ExitCode}).");
                return 1;
            }

            Console.WriteLine($"Service '{ServiceName}' uninstalled.");
            return 0;
        }

        [SupportedOSPlatform("windows")]
        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool ServiceExists() => RunSc("query", ServiceName).ExitCode == 0;

        private static void WaitForServiceRemoved()
        {
            // sc delete is asynchronous; give the SCM a moment to release the name.
            for (var i = 0; i < 10 && ServiceExists(); i++)
            {
                Thread.Sleep(500);
            }
        }

        private static (int ExitCode, string Output) RunSc(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
            {
                Console.Write(output);
            }

            return (process.ExitCode, output);
        }
    }
}
