using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BackupService.Hosting
{
    /// <summary>
    /// Pre-validates that an account + password can perform a Windows <em>service</em> logon
    /// (<c>LOGON32_LOGON_SERVICE</c>), so the installer can report the precise reason a service would
    /// fail to start — a rejected password vs. the "Log on as a service" right not being held vs. a
    /// Windows Hello-only restriction — instead of the SCM's opaque error 1069.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class ServiceLogonValidator
    {
        private const int Logon32LogonService = 5;
        private const int Logon32ProviderDefault = 0;

        /// <summary>Returns 0 if a service logon succeeds, otherwise the Win32 error code from LogonUser.</summary>
        public static int Validate(string account, string password)
        {
            var user = account;
            var domain = ".";
            var slash = account.IndexOf('\\');
            if (slash >= 0)
            {
                domain = account[..slash];
                user = account[(slash + 1)..];
            }

            if (!LogonUser(user, domain, password, Logon32LogonService, Logon32ProviderDefault, out var token))
            {
                return Marshal.GetLastWin32Error();
            }

            CloseHandle(token);
            return 0;
        }

        /// <summary>A human-readable explanation of a LogonUser Win32 error code.</summary>
        public static string Describe(int win32Error) => win32Error switch
        {
            1326 => "the password was rejected (ERROR_LOGON_FAILURE) — wrong password, or Windows Hello-only sign-in is blocking password logon",
            1385 => "the account lacks the 'Log on as a service' right (ERROR_LOGON_TYPE_NOT_GRANTED)",
            1327 => "account restrictions prevent logon, e.g. a blank password or Hello-only sign-in (ERROR_ACCOUNT_RESTRICTION)",
            1330 => "the password has expired (ERROR_PASSWORD_EXPIRED)",
            1331 => "the account is disabled (ERROR_ACCOUNT_DISABLED)",
            _ => new Win32Exception(win32Error).Message,
        };

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string lpszUsername, string? lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
