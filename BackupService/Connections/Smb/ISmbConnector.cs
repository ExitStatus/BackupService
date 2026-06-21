namespace BackupService.Connections.Smb
{
    /// <summary>
    /// Talks to an SMB server (via SMBLibrary) for connectivity testing and remote folder browsing.
    /// Each call opens and disposes its own short-lived session.
    /// </summary>
    public interface ISmbConnector
    {
        /// <summary>Connects, authenticates, opens the configured root folder, and reports the outcome.</summary>
        Task<ConnectionTestResult> TestAsync(SmbConnectionInfo info, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the sub-directory names directly under <paramref name="relativePath"/> (relative to the
        /// share root). Throws <see cref="SmbBrowseException"/> if the share/path can't be reached.
        /// </summary>
        Task<IReadOnlyList<string>> ListDirectoriesAsync(SmbConnectionInfo info, string relativePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates the folder at <paramref name="relativePath"/> (relative to the share root). Throws
        /// <see cref="SmbBrowseException"/> if it can't be created (e.g. it already exists).
        /// </summary>
        Task CreateDirectoryAsync(SmbConnectionInfo info, string relativePath, CancellationToken cancellationToken = default);
    }
}
