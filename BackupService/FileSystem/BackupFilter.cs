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
    /// </list>
    /// Built once per run from a <see cref="Database.FolderPair"/> / <see cref="Database.ArchiveSyncItem"/>'s
    /// filter rows and threaded through the sync/zip engines.
    /// </summary>
    public sealed class BackupFilter
    {
        private readonly List<string> _includeFiles = [];
        private readonly List<string> _excludeFiles = [];
        private readonly List<string> _excludeFolders = [];

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
                    case (FilterDirection.Exclude, FilterKind.File):
                        _excludeFiles.Add(pattern);
                        break;
                }
            }
        }

        /// <summary>True when no rules narrow anything — the engine can skip per-file checks entirely.</summary>
        public bool IsEmpty => _includeFiles.Count == 0 && _excludeFiles.Count == 0 && _excludeFolders.Count == 0;

        /// <summary>An empty include list includes everything; otherwise the name must match an include pattern.</summary>
        public bool IncludesFile(string fileName) =>
            _includeFiles.Count == 0 || _includeFiles.Any(p => WildcardMatcher.IsMatch(p, fileName));

        /// <summary>True if <paramref name="folderName"/> matches an exclude-folder pattern (its subtree is skipped).</summary>
        public bool ExcludesFolder(string folderName) =>
            _excludeFolders.Any(p => WildcardMatcher.IsMatch(p, folderName));

        /// <summary>
        /// Whether a file named <paramref name="fileName"/>, sitting under <paramref name="ancestorFolderNames"/>
        /// (relative to the source root, root-first), is part of the backup.
        /// </summary>
        public bool IsFileInScope(string fileName, IEnumerable<string> ancestorFolderNames)
        {
            if (ancestorFolderNames.Any(ExcludesFolder))
            {
                return false;
            }
            if (_excludeFiles.Any(p => WildcardMatcher.IsMatch(p, fileName)))
            {
                return false;
            }
            return IncludesFile(fileName);
        }

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
