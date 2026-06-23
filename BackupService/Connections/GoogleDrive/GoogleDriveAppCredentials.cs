namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// The app's <b>built-in</b> Google OAuth client (id + secret), sourced from configuration
    /// (<c>GoogleDrive:ClientId</c> / <c>GoogleDrive:ClientSecret</c> — set via user-secrets or an
    /// environment overlay, never committed). When configured, users authorise a Google Drive connection
    /// with one click; otherwise they must supply their own client via the editor's Advanced section.
    /// Registered as a singleton.
    /// </summary>
    public sealed record GoogleDriveAppCredentials(string? ClientId, string? ClientSecret)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }
}
