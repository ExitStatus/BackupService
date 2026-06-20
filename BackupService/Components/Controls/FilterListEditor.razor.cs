using BackupService.Enumerations;
using BackupService.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Edits one tab of include/exclude entries (reused for both the Includes and Excludes tabs of the
    /// folder-pair and archive edit dialogs). A type picker chooses the entry kind — Include ⇒ "Specific
    /// file" / "Wildcard pattern"; Exclude ⇒ "File" / "Folder" — and adding an entry is validated against
    /// both this tab and the other (via <see cref="FilterValidation"/>) so it can't clash.
    /// </summary>
    public partial class FilterListEditor : ComponentBase
    {
        /// <summary>The entries on this tab (mutated in place).</summary>
        [Parameter, EditorRequired]
        public List<FilterEntryModel> Entries { get; set; } = default!;

        /// <summary>The other tab's entries, used only for cross-tab clash checks.</summary>
        [Parameter, EditorRequired]
        public List<FilterEntryModel> OtherEntries { get; set; } = default!;

        [Parameter]
        public FilterDirection Direction { get; set; }

        /// <summary>
        /// The parent's source folder — used to root the folder browser for a <see cref="FilterKind.Path"/>
        /// exclude (browsing is confined to this subtree) and to turn the chosen folder into a relative path.
        /// </summary>
        [Parameter]
        public string? SourceFolder { get; set; }

        private const int PageSize = 8;

        private string _pattern = string.Empty;
        private string _selectedType = "file";
        private string? _error;
        private int _page = 1;
        private bool _browsing;
        private readonly string _radioName = Guid.NewGuid().ToString("N");

        private bool IsInclude => Direction == FilterDirection.Include;

        // The Path type (a relative-path exclude) gets a Browse button rooted at the source folder.
        private bool ShowBrowse => !IsInclude && _selectedType == "path";

        private bool CanBrowse => !string.IsNullOrWhiteSpace(SourceFolder);

        private int TotalPages => Math.Max(1, (int)Math.Ceiling(Entries.Count / (double)PageSize));

        /// <summary>The entries shown on the current page.</summary>
        private IEnumerable<FilterEntryModel> PageItems()
        {
            ClampPage();
            var start = (_page - 1) * PageSize;
            for (var i = start; i < Math.Min(start + PageSize, Entries.Count); i++)
            {
                yield return Entries[i];
            }
        }

        private void ClampPage() => _page = Math.Clamp(_page, 1, TotalPages);

        private void PreviousPage()
        {
            if (_page > 1)
            {
                _page--;
            }
        }

        private void NextPage()
        {
            if (_page < TotalPages)
            {
                _page++;
            }
        }

        private IReadOnlyList<(string Key, string Label)> TypeOptions => IsInclude
            ? [("file", "Specific file"), ("wildcard", "Wildcard pattern")]
            : [("file", "File"), ("folder", "Folder"), ("path", "Path")];

        private string HelpText => IsInclude
            ? "Only files matching an include are backed up. Leave this empty to back up everything."
            : "Files or folders (matched by name in every sub-folder) or a specific path relative to the source root, to leave out of the backup.";

        private string Placeholder => _selectedType switch
        {
            "wildcard" => "*.txt",
            "folder" => "bin",
            "path" => "logs\\archive",
            _ => "report.docx",
        };

        private void Add()
        {
            var pattern = _pattern.Trim();
            var kind = _selectedType switch
            {
                "folder" => FilterKind.Folder,
                "path" => FilterKind.Path,
                _ => FilterKind.File,
            };

            // A "Specific file" include is a literal name; steer wildcards to the Wildcard type.
            if (IsInclude && _selectedType == "file" && WildcardMatcher.IsWildcard(pattern))
            {
                _error = "A specific file can't contain wildcards — choose Wildcard pattern.";
                return;
            }

            // A path is an exact relative location, not a pattern.
            if (_selectedType == "path" && WildcardMatcher.IsWildcard(pattern))
            {
                _error = "A path can't contain wildcards.";
                return;
            }

            var error = FilterValidation.Validate(kind, pattern, Entries, OtherEntries);
            if (error is not null)
            {
                _error = error;
                return;
            }

            Entries.Add(new FilterEntryModel { Kind = kind, Pattern = pattern });
            _pattern = string.Empty;
            _error = null;
            _page = TotalPages; // jump to the page holding the new entry
        }

        private void Remove(FilterEntryModel entry)
        {
            Entries.Remove(entry);
            _error = null;
            ClampPage();
        }

        private void OnKeyDown(KeyboardEventArgs e)
        {
            if (e.Key is "Enter" or "NumpadEnter")
            {
                Add();
            }
        }

        private void OpenBrowse() => _browsing = true;

        // The folder browser returns an absolute path inside the source; store it relative to the source root.
        private void OnPathSelected(string absolutePath)
        {
            _browsing = false;
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                return;
            }

            var relative = Path.GetRelativePath(SourceFolder, absolutePath);
            if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                // Selecting the source root itself, or somehow outside it, isn't a valid sub-path.
                _error = "Choose a folder inside the source folder.";
                return;
            }

            _pattern = relative;
            _error = null;
        }

        // The label shown for an entry: folders/paths read as such; a file with a wildcard char reads "Wildcard".
        private static string TypeLabel(FilterEntryModel e) => e.Kind switch
        {
            FilterKind.Folder => "Folder",
            FilterKind.Path => "Path",
            _ => WildcardMatcher.IsWildcard(e.Pattern) ? "Wildcard" : "File",
        };
    }
}
