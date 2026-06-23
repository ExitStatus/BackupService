using System.ComponentModel.DataAnnotations;
using BackupService.Enumerations;

namespace BackupService.Database
{
    /// <summary>
    /// A configured connection to a remote resource that isn't normally reachable as a local
    /// source/target (e.g. an SMB share). The type-specific settings hang off a 1:1 child table —
    /// add a connection type = add its settings entity + editor.
    /// </summary>
    public class Connection
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public required string Name { get; set; }

        public ConnectionType Type { get; set; }

        public DateTimeOffset DateCreated { get; set; }

        /// <summary>SMB-specific settings; populated when <see cref="Type"/> is <see cref="ConnectionType.Smb"/>.</summary>
        public SmbConnectionSettings? Smb { get; set; }

        /// <summary>Google Drive-specific settings; populated when <see cref="Type"/> is <see cref="ConnectionType.GoogleDrive"/>.</summary>
        public GoogleDriveConnectionSettings? GoogleDrive { get; set; }
    }
}
