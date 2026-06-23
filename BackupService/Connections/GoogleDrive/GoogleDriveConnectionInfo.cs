namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Runtime Google Drive connection details with the <b>decrypted</b> client secret and refresh token.
    /// Built from the stored settings (or from un-saved dialog fields) and used by the connector/engine to
    /// obtain access tokens. Never persisted.
    /// </summary>
    public sealed record GoogleDriveConnectionInfo(
        string ClientId,
        string ClientSecret,
        string RefreshToken,
        string? AccountEmail,
        string? RootFolder);
}
