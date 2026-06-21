namespace BackupService.Security
{
    /// <summary>
    /// Reversible protection for secrets that must be recovered in plaintext (e.g. an SMB password
    /// needed to authenticate). Distinct from one-way password hashing used for the admin login.
    /// </summary>
    public interface ISecretProtector
    {
        /// <summary>Encrypts <paramref name="plaintext"/> and returns base64 ciphertext.</summary>
        string Protect(string plaintext);

        /// <summary>Decrypts base64 ciphertext produced by <see cref="Protect"/> back to plaintext.</summary>
        string Unprotect(string protectedValue);
    }
}
