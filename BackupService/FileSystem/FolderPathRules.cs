namespace BackupService.FileSystem
{
    /// <summary>
    /// What a configured folder must look like for the side it's on. A local folder is a full path on this machine;
    /// a folder on a connection is relative to the connection's root folder. A folder chosen for one kind and kept
    /// after the profile's connection changed to the other is meaningless — worse, a relative path used locally
    /// resolves against the app's working directory, so the backup quietly lands under the app's own folder.
    /// </summary>
    public static class FolderPathRules
    {
        /// <summary>
        /// Why <paramref name="folder"/> doesn't fit its side, or null when it does — phrased to follow "The folder"
        /// (e.g. "'PC1' isn't a full path on this machine").
        /// </summary>
        /// <param name="onConnection">Whether this side is on a connection (false = this machine).</param>
        /// <param name="folder">The configured folder.</param>
        public static string? Problem(bool onConnection, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                // The connection's root folder is a valid choice; a local side always needs a folder.
                return onConnection ? null : "isn't set";
            }

            if (onConnection)
            {
                return IsMachinePath(folder)
                    ? $"'{folder}' is on this machine, not on the connection"
                    : null;
            }

            return Path.IsPathFullyQualified(folder)
                ? null
                : $"'{folder}' isn't a full path on this machine";
        }

        // A drive-letter or UNC path — something only this machine's filesystem can resolve.
        private static bool IsMachinePath(string folder) =>
            (folder.Length >= 2 && char.IsAsciiLetter(folder[0]) && folder[1] == ':')
            || folder.StartsWith(@"\\", StringComparison.Ordinal)
            || folder.StartsWith("//", StringComparison.Ordinal);
    }
}
