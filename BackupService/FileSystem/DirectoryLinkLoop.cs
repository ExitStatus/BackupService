namespace BackupService.FileSystem
{
    /// <summary>
    /// Spots a directory link (junction or symlink) that points at the folder holding it or one above it. A walk that
    /// follows one goes through the same tree again inside itself, without end — Windows' own "Application Data"-style
    /// compatibility junctions are like this. Links to anywhere else are followed as before.
    /// </summary>
    public static class DirectoryLinkLoop
    {
        /// <summary>Where <paramref name="folder"/> links back to, or null when it isn't such a link.</summary>
        public static string? Target(IBackupFileSystem fs, string folder)
        {
            if (fs.GetDirectoryLinkTarget(folder) is not { } target)
            {
                return null;
            }

            // Separator-agnostic, like the engines' other path handling: both sides are compared with '/' throughout.
            var canonical = Canonical(folder);
            var cut = canonical.LastIndexOf('/');
            if (cut <= 0)
            {
                return null;
            }

            var parent = canonical[..cut];
            var targetPath = Canonical(target);
            return string.Equals(parent, targetPath, StringComparison.OrdinalIgnoreCase)
                || parent.StartsWith(targetPath + '/', StringComparison.OrdinalIgnoreCase)
                ? target
                : null;
        }

        private static string Canonical(string path) => path.Replace('\\', '/').TrimEnd('/');
    }
}
