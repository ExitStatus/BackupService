using BackupService.Enumerations;

namespace BackupService.FileSystem
{
    /// <summary>One include/exclude rule in a form independent of which entity it came from.</summary>
    public sealed record FilterRule(FilterDirection Direction, FilterKind Kind, string Pattern);

    /// <summary>
    /// Decides whether a source file is in scope for a backup, given a set of include/exclude
    /// <see cref="FilterRule"/>s. Matching is name-only and case-insensitive (see
    /// <see cref="WildcardMatcher"/>):
    /// <list type="bullet">
    /// <item>An <b>empty include list</b> means every file is included; otherwise only files whose name
    /// matches an include pattern qualify.</item>
    /// <item>An <b>exclude File</b> rule drops any file whose name matches; an <b>exclude Folder</b> rule
    /// drops any file beneath a folder whose name matches (the whole subtree).</item>
    /// <item>An <b>exclude Path</b> rule is matched against the exact location relative to the source root
    /// (not by name everywhere): it drops that file, or that folder and its whole subtree.</item>
    /// </list>
    /// Built once per run from a <see cref="Database.FolderPair"/> / <see cref="Database.ArchiveSyncItem"/>'s
    /// filter rows and threaded through the sync/zip engines.
    /// </summary>
    public sealed class BackupFilter
    {
        private readonly List<string> _includeFiles = [];
        private readonly List<string> _excludeFiles = [];
        private readonly List<string> _excludeFolders = [];
        private readonly List<string> _excludePaths = [];

        public BackupFilter(IEnumerable<FilterRule> rules)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    continue;
                }

                var pattern = rule.Pattern.Trim();
                switch (rule.Direction, rule.Kind)
                {
                    case (FilterDirection.Include, _):
                        _includeFiles.Add(pattern); // includes only ever target files
                        break;
                    case (FilterDirection.Exclude, FilterKind.Folder):
                        _excludeFolders.Add(pattern);
                        break;
                    case (FilterDirection.Exclude, FilterKind.Path):
                        _excludePaths.Add(NormalizePath(pattern));
                        break;
                    case (FilterDirection.Exclude, FilterKind.File):
                        _excludeFiles.Add(pattern);
                        break;
                }
            }
        }

        /// <summary>True when no rules narrow anything — the engine can skip per-file checks entirely.</summary>
        public bool IsEmpty =>
            _includeFiles.Count == 0 && _excludeFiles.Count == 0 && _excludeFolders.Count == 0 && _excludePaths.Count == 0;

        /// <summary>An empty include list includes everything; otherwise the name must match an include pattern.</summary>
        public bool IncludesFile(string fileName) =>
            _includeFiles.Count == 0 || _includeFiles.Any(p => WildcardMatcher.IsMatch(p, fileName));

        /// <summary>True if <paramref name="folderName"/> matches an exclude-folder pattern (its subtree is skipped).</summary>
        public bool ExcludesFolder(string folderName) =>
            _excludeFolders.Any(p => WildcardMatcher.IsMatch(p, folderName));

        /// <summary>
        /// True if the item at <paramref name="relativeSegments"/> (relative to the source root, root-first)
        /// is an excluded path — it equals an exclude-path rule (that exact file/folder) or sits beneath one
        /// (the subtree). Unlike <see cref="ExcludesFolder"/> this matches the exact location, not a name.
        /// </summary>
        public bool ExcludesPath(IEnumerable<string> relativeSegments)
        {
            if (_excludePaths.Count == 0)
            {
                return false;
            }

            var rel = string.Join('\\', relativeSegments);
            return _excludePaths.Any(x =>
                rel.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith(x + '\\', StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether a file named <paramref name="fileName"/>, sitting under <paramref name="ancestorFolderNames"/>
        /// (relative to the source root, root-first), is part of the backup.
        /// </summary>
        public bool IsFileInScope(string fileName, IEnumerable<string> ancestorFolderNames)
        {
            var ancestors = ancestorFolderNames as IReadOnlyList<string> ?? ancestorFolderNames.ToList();
            if (ancestors.Any(ExcludesFolder))
            {
                return false;
            }
            if (_excludeFiles.Any(p => WildcardMatcher.IsMatch(p, fileName)))
            {
                return false;
            }
            if (ExcludesPath([.. ancestors, fileName]))
            {
                return false;
            }
            return IncludesFile(fileName);
        }

        /// <summary>Normalises a relative path for comparison: forward slashes to back, no surrounding separators.</summary>
        private static string NormalizePath(string path) => path.Replace('/', '\\').Trim('\\').Trim();

        /// <summary>
        /// Whether a file at <paramref name="relativePath"/> (relative to the source root, using either
        /// separator) is in scope — splits the path into folders + name and applies
        /// <see cref="IsFileInScope"/>. Used by the flat zip enumeration.
        /// </summary>
        public bool IsRelativePathInScope(string relativePath)
        {
            var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return true;
            }

            var fileName = segments[^1];
            var ancestors = segments[..^1];
            return IsFileInScope(fileName, ancestors);
        }
    }
}
