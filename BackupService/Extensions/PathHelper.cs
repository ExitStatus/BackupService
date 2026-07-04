namespace BackupService.Extensions
{
    /// <summary>
    /// Separator-agnostic path helpers. Paths in this app can arrive with either separator: local paths use the OS
    /// separator, but remote filesystems (SMB, Google Drive) use backslash name-paths regardless of the host OS. So
    /// extracting a file name must not go through <see cref="System.IO.Path"/>, whose separator is fixed per
    /// platform — e.g. on Linux <c>Path.GetFileName(@"folder\file.zip")</c> does not treat the backslash as a
    /// separator and returns the whole string, which then fails to parse.
    /// </summary>
    public static class PathHelper
    {
        /// <summary>The final segment of <paramref name="path"/>, splitting on either '/' or '\' on any OS.</summary>
        public static string GetLeafName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var index = path.LastIndexOfAny(['/', '\\']);
            return index < 0 ? path : path[(index + 1)..];
        }
    }
}
