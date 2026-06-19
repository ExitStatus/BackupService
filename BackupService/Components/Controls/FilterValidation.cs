using BackupService.Enumerations;
using BackupService.FileSystem;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Clash validation for adding an include/exclude entry, kept as a pure static helper so it can be
    /// unit-tested without a rendered component. Enforces the three rules the user asked for:
    /// no duplicate on the same tab, no identical value on the opposite tab (include/exclude
    /// contradiction), and no literal already covered by an existing wildcard on the same tab.
    /// </summary>
    public static class FilterValidation
    {
        /// <summary>Returns an error message to show, or <c>null</c> if the entry can be added.</summary>
        public static string? Validate(
            FilterKind kind,
            string pattern,
            IReadOnlyList<FilterEntryModel> sameTab,
            IReadOnlyList<FilterEntryModel> otherTab)
        {
            var trimmed = (pattern ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return "Enter a file name or pattern.";
            }

            // (a) duplicate on the same tab
            if (sameTab.Any(e => string.Equals(e.Pattern, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{trimmed}' is already in this list.";
            }

            // (b) the same value already exists on the opposite tab (include vs exclude contradiction)
            if (otherTab.Any(e => string.Equals(e.Pattern, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{trimmed}' is already on the other tab.";
            }

            // (c) a literal entry already covered by an existing wildcard of the same kind on this tab
            if (!WildcardMatcher.IsWildcard(trimmed))
            {
                var covering = sameTab.FirstOrDefault(e =>
                    e.Kind == kind &&
                    WildcardMatcher.IsWildcard(e.Pattern) &&
                    WildcardMatcher.IsMatch(e.Pattern, trimmed));
                if (covering is not null)
                {
                    return $"'{trimmed}' is already covered by '{covering.Pattern}'.";
                }
            }

            return null;
        }
    }
}
