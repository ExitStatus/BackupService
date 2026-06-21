using System.Security.Cryptography;
using System.Text;

namespace BackupService.Security
{
    /// <summary>
    /// <see cref="ISecretProtector"/> backed by Windows DPAPI at <see cref="DataProtectionScope.LocalMachine"/>
    /// scope, so any account on this machine (including the service's LocalSystem) can decrypt. The
    /// ciphertext is machine-bound: secrets do not port to another machine and must be re-entered there.
    /// Windows-only — acceptable since the service runs as a Windows Service.
    /// </summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }

        public string Unprotect(string protectedValue)
        {
            ArgumentNullException.ThrowIfNull(protectedValue);
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
