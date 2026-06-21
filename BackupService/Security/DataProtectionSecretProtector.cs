using Microsoft.AspNetCore.DataProtection;

namespace BackupService.Security
{
    /// <summary>
    /// <see cref="ISecretProtector"/> backed by <b>ASP.NET Core Data Protection</b> — reversible
    /// encryption that works on both Windows and Linux (on Windows the key ring is itself DPAPI-protected;
    /// on Linux it is persisted to the key directory). The key ring is persisted under the app's data
    /// directory with a fixed application name, so protected secrets stay decryptable across restarts.
    /// </summary>
    public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
    {
        // A stable, versioned purpose string isolates these secrets from any other protected payloads.
        private readonly IDataProtector _protector = provider.CreateProtector("BackupService.Connections.Smb.Password.v1");

        public string Protect(string plaintext) => _protector.Protect(plaintext);

        public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
    }
}
