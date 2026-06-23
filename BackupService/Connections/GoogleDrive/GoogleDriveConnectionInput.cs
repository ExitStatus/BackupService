namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Google Drive settings supplied when creating or updating a connection. When
    /// <see cref="UseBuiltInClient"/> is true the app's built-in OAuth client is used (Client ID/secret are
    /// ignored); otherwise the supplied <see cref="ClientId"/>/<see cref="ClientSecret"/> are used.
    /// <see cref="ClientSecret"/> and <see cref="RefreshToken"/> are the plaintext as captured; null/empty on
    /// an edit means "keep the stored value unchanged".
    /// </summary>
    public sealed record GoogleDriveConnectionInput(
        bool UseBuiltInClient,
        string ClientId,
        string? ClientSecret,
        string? RefreshToken,
        string? AccountEmail,
        string? RootFolder);
}
