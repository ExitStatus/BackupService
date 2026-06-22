using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using BackupService.Enumerations;

namespace BackupService.Hosting
{
    /// <summary>
    /// Installs/uninstalls this executable as a Windows Service via <c>sc.exe</c>.
    /// Invoked from the command line (<c>--install</c> / <c>--uninstall</c>); requires
    /// an elevated (Administrator) prompt. <c>--install</c> must be qualified with the account to run
    /// under: <c>--server</c> (LocalSystem) or <c>--user</c> (the current interactive user, so per-user
    /// resources such as OneDrive are reachable).
    /// </summary>
    public static class WindowsServiceInstaller
    {
        public const string ServiceName = "BackupService";
        public const string DisplayName = "Backup Service";

        public static int Install(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Service install is only supported on Windows.");
                return 1;
            }

            var hasServer = args.Contains("--server");
            var hasUser = args.Contains("--user");
            if (hasServer == hasUser)
            {
                Console.Error.WriteLine(hasServer
                    ? "Specify only one of --server or --user, not both."
                    : "An account must be specified: --install --server (LocalSystem) or --install --user (the current user).");
                return 1;
            }

            var accountKind = hasUser ? ServiceAccountKind.User : ServiceAccountKind.Server;

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

            // Resolve the account (and, for a user account, prompt for its password — sc.exe needs the
            // plaintext to register the logon). LocalSystem needs no password.
            string account = "LocalSystem";
            string? password = null;
            if (accountKind == ServiceAccountKind.User)
            {
                account = WindowsIdentity.GetCurrent().Name;
                Console.WriteLine($"Installing to run as the current user '{account}'.");
                // Shown as typed/pasted so a pasted password can be verified (local admin console only).
                Console.Write($"Enter the Windows password for {account} (visible): ");
                password = Console.ReadLine() ?? string.Empty;
                if (string.IsNullOrEmpty(password))
                {
                    Console.Error.WriteLine("A password is required to install the service under a user account.");
                    return 1;
                }

                // A user account cannot start a service without the "Log on as a service" right; grant it
                // up front so the validation below reflects the right being held.
                try
                {
                    LogonAsServiceRight.Grant(account);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: could not grant 'Log on as a service' to {account}: {ex.Message}");
                }

                // Verify the credentials can actually perform a service logon before involving the SCM, so a
                // failure gives the precise reason rather than the SCM's opaque error 1069 at start time.
                var logonError = ServiceLogonValidator.Validate(account, password);
                if (logonError != 0)
                {
                    Console.Error.WriteLine($"The service logon for '{account}' would fail: {ServiceLogonValidator.Describe(logonError)}.");
                    if (logonError is 1326 or 1327)
                    {
                        Console.Error.WriteLine(
                            "This is a Microsoft account, so use its online password (account.microsoft.com), not your PIN. If you only");
                        Console.Error.WriteLine(
                            "sign in with Windows Hello, turn off Settings > Accounts > Sign-in options > 'For improved security, only allow");
                        Console.Error.WriteLine(
                            "Windows Hello sign-in for Microsoft accounts', or use '--install --server' to run as LocalSystem instead.");
                    }
                    return 1;
                }
            }

            // Replace any existing service.
            if (ServiceExists())
            {
                Console.WriteLine($"Service '{ServiceName}' already exists - replacing it.");
                RunSc("stop", ServiceName);
                RunSc("delete", ServiceName);
                WaitForServiceRemoved();
            }

            var create = CreateService(exePath, account, password);

            // 1072 = "marked for deletion": the old service hasn't fully gone yet; retry once.
            if (create.ExitCode == 1072)
            {
                WaitForServiceRemoved();
                create = CreateService(exePath, account, password);
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

                // 1069 = ERROR_SERVICE_LOGON_FAILED: the SCM rejected the account credentials.
                if (start.ExitCode == 1069 && accountKind == ServiceAccountKind.User)
                {
                    Console.Error.WriteLine(
                        $"Logon failure for '{account}'. The service is registered but the password was not accepted.");
                    Console.Error.WriteLine(
                        "If this is a Microsoft-account profile, enter your Microsoft account password (account.microsoft.com),");
                    Console.Error.WriteLine(
                        "NOT your Windows Hello PIN. Passwordless (Windows Hello-only) accounts cannot run a service.");
                    Console.Error.WriteLine(
                        $"Re-run '--install --user' with the correct password, or set the password in services.msc for '{ServiceName}'.");
                }
                return 1;
            }

            Console.WriteLine($"Service '{ServiceName}' installed (autostart, {account}) and started.");
            return 0;
        }

        // sc.exe quirk: each "key=" and its value are separate tokens (== "key= value").
        private static (int ExitCode, string Output) CreateService(string exePath, string account, string? password)
        {
            var arguments = new List<string>
            {
                "create", ServiceName,
                "binPath=", exePath,
                "start=", "auto",
                "obj=", account,
            };
            if (password is not null)
            {
                arguments.Add("password=");
                arguments.Add(password);
            }
            arguments.Add("DisplayName=");
            arguments.Add(DisplayName);

            return RunSc(arguments.ToArray());
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
