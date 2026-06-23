using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// Google Drive-specific settings for a <see cref="Connection"/> (1:1, keyed by and cascade-deleted
    /// with the connection). The user supplies their own OAuth client (Client ID + Secret) and authorises
    /// once via the in-app consent flow; we keep the resulting refresh token. The client secret and refresh
    /// token are stored encrypted at rest (Data Protection, base64) and only decrypted to obtain access
    /// tokens — see <c>ISecretProtector</c>.
    /// </summary>
    public class GoogleDriveConnectionSettings
    {
        /// <summary>Primary key and foreign key to the owning <see cref="Connection"/>.</summary>
        public int ConnectionId { get; set; }

        public Connection? Connection { get; set; }

        /// <summary>
        /// When true the connection was authorised with the app's built-in OAuth client (its id/secret come
        /// from config at run time); <see cref="ClientId"/>/<see cref="ClientSecretEncrypted"/> are then empty.
        /// When false the connection uses its own (advanced) client stored below.
        /// </summary>
        public bool UsesBuiltInClient { get; set; }

        /// <summary>The OAuth 2.0 client id of the user's own Google Cloud project (empty for the built-in client).</summary>
        [MaxLength(512)]
        public required string ClientId { get; set; }

        /// <summary>Encrypted (Data Protection, base64) OAuth client secret (empty for the built-in client).</summary>
        [MaxLength(4096)]
        public required string ClientSecretEncrypted { get; set; }

        /// <summary>Encrypted (Data Protection, base64) OAuth refresh token captured during consent.</summary>
        [MaxLength(4096)]
        public required string RefreshTokenEncrypted { get; set; }

        /// <summary>The authorised account's email, for display only (never used to authenticate).</summary>
        [MaxLength(320)]
        public string? AccountEmail { get; set; }

        /// <summary>
        /// The folder the connection is rooted at, as a name-path relative to My Drive root (e.g.
        /// <c>Backups\Archives</c>); null/empty means My Drive root. Per-side paths are name-paths relative
        /// to this folder. Drive is id-based, so the path is resolved to folder ids at run time.
        /// </summary>
        [MaxLength(1024)]
        public string? RootFolder { get; set; }
    }
}
