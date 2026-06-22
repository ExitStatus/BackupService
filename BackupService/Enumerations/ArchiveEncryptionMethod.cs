using System.ComponentModel;
using BackupService.Extensions;

namespace BackupService.Enumerations
{
    /// <summary>
    /// How a password-protected <see cref="ProfileType.ArchiveSync"/> archive is encrypted.
    /// </summary>
    public enum ArchiveEncryptionMethod
    {
        [Description("AES-256")]
        [HelpText("Strong, modern encryption. Opens in 7-Zip / WinRAR / PeaZip, but not in Windows Explorer's built-in zip.")]
        Aes256 = 0,

        [Description("ZipCrypto (legacy)")]
        [HelpText("Weak legacy encryption (easily cracked), but opens everywhere — including Windows Explorer's built-in Extract All.")]
        ZipCrypto = 1,
    }
}
