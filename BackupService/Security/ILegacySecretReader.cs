namespace BackupService.Security
{
    /// <summary>
    /// Reads a secret stored in a <b>legacy</b> (pre-Data-Protection) format, so the one-time migration can
    /// re-encrypt it with the current <see cref="ISecretProtector"/>. Returns false when the value isn't in
    /// the legacy format (or can't be read on this platform).
    /// </summary>
    public interface ILegacySecretReader
    {
        bool TryRead(string storedValue, out string plaintext);
    }
}
