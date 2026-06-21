using System.Security.Cryptography;
using System.Text;

namespace BackupService.Security
{
    /// <summary>
    /// Reads passwords stored by the old Windows-DPAPI scheme (base64 of
    /// <see cref="ProtectedData"/>.<c>Protect(..., LocalMachine)</c>), so they can be migrated to the new
    /// cross-platform Data Protection format. Windows-only by nature; returns false on any other platform or
    /// when the value isn't a valid DPAPI blob (e.g. it's already in the new format).
    /// </summary>
    public sealed class DpapiLegacySecretReader : ILegacySecretReader
    {
        public bool TryRead(string storedValue, out string plaintext)
        {
            plaintext = string.Empty;

            // The legacy data only exists on the Windows deployment; DPAPI isn't available elsewhere.
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                var cipher = Convert.FromBase64String(storedValue);
                var bytes = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.LocalMachine);
                plaintext = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch
            {
                // Not a DPAPI value (already migrated, or unreadable) — let the caller leave it as-is.
                return false;
            }
        }
    }
}
