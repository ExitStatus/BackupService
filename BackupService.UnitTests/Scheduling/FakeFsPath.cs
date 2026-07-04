namespace BackupService.UnitTests.Scheduling
{
    /// <summary>
    /// Separator-agnostic path helpers shared by the in-memory <c>FakeFileSystem</c> fakes. The tests spell their
    /// paths Windows-style (<c>C:\src\a.txt</c>), but the real engines build child paths with
    /// <see cref="System.IO.Path"/>, whose separator is the host OS's (backslash on Windows, forward slash on
    /// Linux). So the fake must compare/store paths independent of which separator appears, and must hand the
    /// engine paths that use the host separator (so the engine's own <c>Path.GetFileName</c>/<c>Path.Combine</c>
    /// work). These helpers provide that: <see cref="Comparer"/> treats the two separators as equivalent for
    /// dictionary keys, <see cref="Parent"/> is a separator-agnostic directory-name, and <see cref="Norm"/>
    /// rewrites a path to the host separator on the way out to the engine.
    /// </summary>
    internal static class FakeFsPath
    {
        /// <summary>Key comparer that treats '\' and '/' as the same separator (and is case-insensitive).</summary>
        public static readonly IEqualityComparer<string> Comparer = new SeparatorInsensitiveComparer();

        /// <summary>Rewrites both separators to the host <see cref="Path.DirectorySeparatorChar"/>.</summary>
        public static string Norm(string path) =>
            path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        /// <summary>The parent directory of <paramref name="path"/>, splitting on either separator on any OS.</summary>
        public static string Parent(string path)
        {
            var index = path.LastIndexOfAny(['\\', '/']);
            return index < 0 ? string.Empty : path[..index];
        }

        /// <summary>True when <paramref name="path"/> sits strictly beneath <paramref name="directory"/>.</summary>
        public static bool IsUnder(string path, string directory)
        {
            var prefix = Canon(directory).TrimEnd('/') + "/";
            return Canon(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string Canon(string path) => path.Replace('\\', '/');

        private sealed class SeparatorInsensitiveComparer : IEqualityComparer<string>
        {
            public bool Equals(string? x, string? y) =>
                string.Equals(Canon(x ?? string.Empty), Canon(y ?? string.Empty), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(string obj) =>
                Canon(obj).GetHashCode(StringComparison.OrdinalIgnoreCase);
        }
    }
}
