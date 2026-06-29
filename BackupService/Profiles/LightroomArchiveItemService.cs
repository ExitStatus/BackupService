using BackupService.Database;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="ILightroomArchiveItemService"/>. A stateless helper over the tracked
    /// <see cref="Profile"/> entity graph (no DbContext), mirroring <see cref="InstantSyncItemService"/>.
    /// </summary>
    public sealed class LightroomArchiveItemService : ILightroomArchiveItemService
    {
        public void Add(Profile profile, IReadOnlyList<LightroomArchiveInput> inputs)
        {
            foreach (var input in inputs)
            {
                profile.LightroomArchiveItems.Add(NewItem(input));
            }
        }

        public IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<LightroomArchiveInput> inputs)
        {
            // Snapshot the original items before mutating so we can describe what changed.
            var oldItems = profile.LightroomArchiveItems.ToDictionary(
                i => i.Id,
                i => new ItemSnapshot(i.Name, i.SourceFolder, i.TargetFolder, i.DebounceMilliseconds, i.IncludeSubFolders, i.AllowDeletions));

            // Remove items the user deleted (not present by id in the new set).
            var keptIds = inputs.Where(i => i.Id != 0).Select(i => i.Id).ToHashSet();
            foreach (var removed in profile.LightroomArchiveItems.Where(i => !keptIds.Contains(i.Id)).ToList())
            {
                profile.LightroomArchiveItems.Remove(removed);
            }

            // Update matched items and add new ones.
            foreach (var input in inputs)
            {
                var existing = input.Id != 0
                    ? profile.LightroomArchiveItems.FirstOrDefault(i => i.Id == input.Id)
                    : null;

                if (existing is null)
                {
                    profile.LightroomArchiveItems.Add(NewItem(input));
                }
                else
                {
                    existing.Name = input.Name;
                    existing.SourceFolder = input.SourceFolder;
                    existing.TargetFolder = input.TargetFolder;
                    existing.DebounceMilliseconds = input.DebounceMilliseconds;
                    existing.IncludeSubFolders = input.IncludeSubFolders;
                    existing.AllowDeletions = input.AllowDeletions;
                }
            }

            return DescribeChanges(oldItems, keptIds, inputs);
        }

        public IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<LightroomArchiveInput> inputs)
        {
            var lines = new List<string>(inputs.Count * 6);
            foreach (var item in inputs)
            {
                lines.Add($"Lightroom archive: {item.Name}");
                lines.Add($"Source: {item.SourceFolder}");
                lines.Add($"Target: {item.TargetFolder}");
                lines.Add($"Debounce: {DebounceText(item.DebounceMilliseconds)}");
                lines.Add($"Include sub-folders: {YesNo(item.IncludeSubFolders)}");
                lines.Add($"Allow deletions: {YesNo(item.AllowDeletions)}");
            }
            return lines;
        }

        private static IReadOnlyList<string> DescribeChanges(
            IReadOnlyDictionary<int, ItemSnapshot> oldItems,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<LightroomArchiveInput> inputs)
        {
            var changes = new List<string>();

            // Removed items: present before, not kept by id now.
            foreach (var (itemId, old) in oldItems)
            {
                if (!keptIds.Contains(itemId))
                {
                    changes.Add($"Lightroom archive '{old.Name}' removed");
                }
            }

            // Added or modified items.
            foreach (var input in inputs)
            {
                if (input.Id == 0 || !oldItems.TryGetValue(input.Id, out var old))
                {
                    changes.Add($"Lightroom archive '{input.Name}' added ({input.SourceFolder} -> {input.TargetFolder})");
                    continue;
                }

                if (old.Name != input.Name)
                {
                    changes.Add($"Lightroom archive '{old.Name}' renamed to '{input.Name}'");
                }
                if (old.SourceFolder != input.SourceFolder)
                {
                    changes.Add($"Lightroom archive '{input.Name}' source changed from '{old.SourceFolder}' to '{input.SourceFolder}'");
                }
                if (old.TargetFolder != input.TargetFolder)
                {
                    changes.Add($"Lightroom archive '{input.Name}' target changed from '{old.TargetFolder}' to '{input.TargetFolder}'");
                }
                if (old.DebounceMilliseconds != input.DebounceMilliseconds)
                {
                    changes.Add($"Lightroom archive '{input.Name}' debounce changed from '{DebounceText(old.DebounceMilliseconds)}' to '{DebounceText(input.DebounceMilliseconds)}'");
                }
                if (old.IncludeSubFolders != input.IncludeSubFolders)
                {
                    changes.Add($"Lightroom archive '{input.Name}' include sub-folders changed from '{YesNo(old.IncludeSubFolders)}' to '{YesNo(input.IncludeSubFolders)}'");
                }
                if (old.AllowDeletions != input.AllowDeletions)
                {
                    changes.Add($"Lightroom archive '{input.Name}' allow deletions changed from '{YesNo(old.AllowDeletions)}' to '{YesNo(input.AllowDeletions)}'");
                }
            }

            return changes;
        }

        private static LightroomArchiveItem NewItem(LightroomArchiveInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            DebounceMilliseconds = input.DebounceMilliseconds,
            IncludeSubFolders = input.IncludeSubFolders,
            AllowDeletions = input.AllowDeletions,
        };

        private static string DebounceText(int milliseconds) =>
            $"{milliseconds / 1000.0:0.##}s";

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private sealed record ItemSnapshot(
            string Name,
            string SourceFolder,
            string TargetFolder,
            int DebounceMilliseconds,
            bool IncludeSubFolders,
            bool AllowDeletions);
    }
}
