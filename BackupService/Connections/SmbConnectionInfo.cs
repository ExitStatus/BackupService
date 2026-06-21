namespace BackupService.Connections
{
    /// <summary>
    /// Runtime SMB connection details with the <b>decrypted</b> password. Built from the stored
    /// settings (or from un-saved dialog fields) and used by the connector/engine to authenticate.
    /// Never persisted.
    /// </summary>
    public sealed record SmbConnectionInfo(
        string Host,
        int Port,
        string Share,
        string? Domain,
        string Username,
        string Password,
        string? RootFolder);
}
