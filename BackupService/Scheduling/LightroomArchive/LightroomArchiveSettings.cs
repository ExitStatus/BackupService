using BackupService.Database;

namespace BackupService.Scheduling
{
    /// <summary>
    /// The profile-level LightroomArchive configuration, shared by all of a profile's items: the local
    /// Lightroom catalog folder scanned for raw sidecars, the set of raw extensions to match (normalised to
    /// leading-dot lowercase), and the name of the RAW sub-folder the matches are copied into.
    /// </summary>
    public sealed record LightroomArchiveSettings(string LightroomFolder, IReadOnlySet<string> RawExtensions, string RawFolderName)
    {
        public const string DefaultRawFormats = ".DNG,.ARW";
        public const string DefaultRawFolderName = "RAW";

        /// <summary>Builds the settings from a profile's stored values, applying defaults for blanks.</summary>
        public static LightroomArchiveSettings FromProfile(Profile profile) =>
            new(
                profile.LightroomFolder ?? string.Empty,
                ParseExtensions(profile.RawFormats),
                string.IsNullOrWhiteSpace(profile.RawFolderName) ? DefaultRawFolderName : profile.RawFolderName.Trim());

        /// <summary>Parses a comma-separated extension list into a normalised (leading-dot, lowercase) set.</summary>
        public static IReadOnlySet<string> ParseExtensions(string? rawFormats)
        {
            var source = string.IsNullOrWhiteSpace(rawFormats) ? DefaultRawFormats : rawFormats;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in source.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part.StartsWith('.') ? part : "." + part);
            }
            return set;
        }
    }
}
