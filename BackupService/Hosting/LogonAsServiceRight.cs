using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace BackupService.Hosting
{
    /// <summary>
    /// Grants the "Log on as a service" privilege (<c>SeServiceLogonRight</c>) to an account via the LSA
    /// API. A Windows service installed to run under a normal user account cannot start without this right,
    /// so the user-account install path (<c>--install --user</c>) grants it before starting the service.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class LogonAsServiceRight
    {
        private const string SeServiceLogonRight = "SeServiceLogonRight";
        private const int PolicyCreateAccount = 0x00000010;
        private const int PolicyLookupNames = 0x00000800;

        /// <summary>
        /// Grants <c>SeServiceLogonRight</c> to <paramref name="accountName"/> (e.g. <c>DOMAIN\user</c>).
        /// Idempotent — granting a right the account already holds is a no-op. Throws on failure.
        /// </summary>
        public static void Grant(string accountName)
        {
            var sid = (SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier));
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);

            var systemName = default(LSA_UNICODE_STRING);
            var attributes = default(LSA_OBJECT_ATTRIBUTES);

            var status = LsaOpenPolicy(ref systemName, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policyHandle);
            ThrowIfError(status, nameof(LsaOpenPolicy));
            try
            {
                var rights = new[] { ToLsaString(SeServiceLogonRight) };
                try
                {
                    status = LsaAddAccountRights(policyHandle, sidBytes, rights, 1);
                    ThrowIfError(status, nameof(LsaAddAccountRights));
                }
                finally
                {
                    Marshal.FreeHGlobal(rights[0].Buffer);
                }
            }
            finally
            {
                LsaClose(policyHandle);
            }
        }

        private static LSA_UNICODE_STRING ToLsaString(string value) => new()
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * 2),
            MaximumLength = (ushort)((value.Length + 1) * 2),
        };

        private static void ThrowIfError(uint ntStatus, string api)
        {
            if (ntStatus == 0)
            {
                return;
            }

            var winError = LsaNtStatusToWinError(ntStatus);
            throw new Win32Exception((int)winError, $"{api} failed (NTSTATUS 0x{ntStatus:X8}).");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaOpenPolicy(ref LSA_UNICODE_STRING SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, int DesiredAccess, out IntPtr PolicyHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaAddAccountRights(IntPtr PolicyHandle, byte[] AccountSid, LSA_UNICODE_STRING[] UserRights, uint CountOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr PolicyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaNtStatusToWinError(uint Status);
    }
}
