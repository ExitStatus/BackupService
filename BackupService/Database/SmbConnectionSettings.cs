using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// SMB-specific settings for a <see cref="Connection"/> (1:1, keyed by and cascade-deleted with
    /// the connection). The password is stored encrypted at rest (DPAPI, base64) and only decrypted
    /// to authenticate — see <c>ISecretProtector</c>.
    /// </summary>
    public class SmbConnectionSettings
    {
        /// <summary>Primary key and foreign key to the owning <see cref="Connection"/>.</summary>
        public int ConnectionId { get; set; }

        public Connection? Connection { get; set; }

        [MaxLength(256)]
        public required string Host { get; set; }

        public int Port { get; set; } = 445;

        [MaxLength(256)]
        public required string ShareName { get; set; }

        [MaxLength(256)]
        public string? Domain { get; set; }

        [MaxLength(256)]
        public required string Username { get; set; }

        /// <summary>DPAPI-encrypted password as base64.</summary>
        [MaxLength(4096)]
        public required string PasswordEncrypted { get; set; }

        /// <summary>Root folder on the share (path relative to the share root); null/empty = share root.</summary>
        [MaxLength(1024)]
        public string? RootFolder { get; set; }
    }
}
