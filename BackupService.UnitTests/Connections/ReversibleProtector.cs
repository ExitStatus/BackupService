using System.Text;
using BackupService.Security;

namespace BackupService.UnitTests.Connections
{
    /// <summary>
    /// A deterministic, reversible <see cref="ISecretProtector"/> for tests — base64 of the bytes with
    /// a marker prefix so the "ciphertext" is clearly distinct from the plaintext. Not real encryption.
    /// </summary>
    public sealed class ReversibleProtector : ISecretProtector
    {
        private const string Prefix = "enc:";

        public string Protect(string plaintext) =>
            Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
    }
}
