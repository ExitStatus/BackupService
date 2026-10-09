using System.IO.Enumeration;

namespace BackupService.FileSystem
{
    /// <summary>
    /// Name-only, case-insensitive wildcard matching for backup include/exclude rules, built on the
    /// BCL <see cref="FileSystemName.MatchesSimpleExpression(System.ReadOnlySpan{char}, System.ReadOnlySpan{char}, bool)"/>
    /// (supports <c>*</c>, <c>?</c> and <c>[...]</c>). Shared by <see cref="BackupFilter"/> and the
    /// dialog-side filter validation.
    /// </summary>
    public static class WildcardMatcher
    {
        /// <summary>True if <paramref name="name"/> matches the wildcard <paramref name="pattern"/> (case-insensitive).</summary>
        /// <remarks>
        /// <c>*.*</c> means "every file", as it does everywhere on Windows — a simple expression would require a dot,
        /// so an include of <c>*.*</c> quietly left out files with no extension (Makefile, LICENSE, …).
        /// </remarks>
        public static bool IsMatch(string pattern, string name) =>
            pattern == "*.*" || FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

        /// <summary>True if <paramref name="pattern"/> contains a wildcard character (<c>*</c> or <c>?</c>).</summary>
        public static bool IsWildcard(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;
    }
}
