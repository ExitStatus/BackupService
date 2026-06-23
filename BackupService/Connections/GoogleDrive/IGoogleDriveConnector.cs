using BackupService.Connections.Smb;

namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Talks to Google Drive for connectivity testing and remote folder browsing. The Google counterpart of
    /// <see cref="ISmbConnector"/>: paths are name-paths relative to My Drive root (backslash-separated),
    /// resolved to folder ids per call. Each call builds a short-lived <see cref="Google.Apis.Drive.v3.DriveService"/>.
    /// </summary>
    public interface IGoogleDriveConnector
    {
        /// <summary>Authenticates, reads the account, and confirms the configured root is reachable.</summary>
        Task<ConnectionTestResult> TestAsync(GoogleDriveConnectionInfo info, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the sub-folder names directly under <paramref name="relativePath"/> (a name-path relative to
        /// My Drive root). Throws when the path can't be reached.
        /// </summary>
        Task<IReadOnlyList<string>> ListDirectoriesAsync(GoogleDriveConnectionInfo info, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>Creates the folder at <paramref name="relativePath"/> (its parent must already exist).</summary>
        Task CreateDirectoryAsync(GoogleDriveConnectionInfo info, string relativePath, CancellationToken cancellationToken = default);
    }
}
