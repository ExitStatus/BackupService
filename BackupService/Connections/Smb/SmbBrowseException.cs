namespace BackupService.Connections.Smb
{
    /// <summary>Thrown when an SMB browse operation can't reach the server, share or path.</summary>
    public sealed class SmbBrowseException(string message) : Exception(message);
}
