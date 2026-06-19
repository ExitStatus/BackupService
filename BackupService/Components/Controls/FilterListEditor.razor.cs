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

        private string _pattern = string.Empty;
        private string _selectedType = "file";
        private string? _error;
        private readonly string _radioName = Guid.NewGuid().ToString("N");

        private bool IsInclude => Direction == FilterDirection.Include;

        private IReadOnlyList<(string Key, string Label)> TypeOptions => IsInclude
            ? [("file", "Specific file"), ("wildcard", "Wildcard pattern")]
            : [("file", "File"), ("folder", "Folder")];

        private string HelpText => IsInclude
            ? "Only files matching an include are backed up. Leave this empty to back up everything."
            : "Files or folders to leave out of the backup.";

        private string Placeholder => _selectedType switch
        {
            "wildcard" => "*.txt",
            "folder" => "bin",
            _ => "report.docx",
        };

        private string EmptyText => IsInclude
            ? "No includes — all files are backed up."
            : "No excludes.";

        private void Add()
        {
            var pattern = _pattern.Trim();
            var kind = _selectedType == "folder" ? FilterKind.Folder : FilterKind.File;

            // A "Specific file" include is a literal name; steer wildcards to the Wildcard type.
            if (IsInclude && _selectedType == "file" && WildcardMatcher.IsWildcard(pattern))
            {
                _error = "A specific file can't contain wildcards — choose Wildcard pattern.";
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
        }

        private void Remove(FilterEntryModel entry)
        {
            Entries.Remove(entry);
            _error = null;
        }

        private void OnKeyDown(KeyboardEventArgs e)
        {
            if (e.Key is "Enter" or "NumpadEnter")
            {
                Add();
            }
        }

        // The label shown for an entry: folders are folders; a file with a wildcard char reads "Wildcard".
        private static string TypeLabel(FilterEntryModel e) => e.Kind == FilterKind.Folder
            ? "Folder"
            : WildcardMatcher.IsWildcard(e.Pattern) ? "Wildcard" : "File";
    }
}
