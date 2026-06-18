using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IArchiveSyncItemService"/>. A stateless helper over the tracked
    /// <see cref="Profile"/> entity graph (no DbContext), mirroring <see cref="FolderPairService"/> and
    /// <see cref="InstantSyncItemService"/>. The per-item <c>RunCount</c> (which drives the GFS
    /// promotion cadence) is owned by the run, so it is left untouched here.
    /// </summary>
    public sealed class ArchiveSyncItemService : IArchiveSyncItemService
    {
        public void Add(Profile profile, IReadOnlyList<ArchiveSyncInput> inputs)
        {
            foreach (var input in inputs)
            {
                profile.ArchiveSyncItems.Add(NewItem(input));
            }
        }

        public IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<ArchiveSyncInput> inputs)
        {
            // Snapshot the original items before mutating so we can describe what changed.
            var oldItems = profile.ArchiveSyncItems.ToDictionary(
                i => i.Id,
                i => new ItemSnapshot(i.Name, i.SourceFolder, i.TargetFolder, i.FileName, i.IncludeSubFolders, i.RetentionMode, i.RetentionCount, i.MaxLevels));

            // Remove items the user deleted (not present by id in the new set).
            var keptIds = inputs.Where(i => i.Id != 0).Select(i => i.Id).ToHashSet();
            foreach (var removed in profile.ArchiveSyncItems.Where(i => !keptIds.Contains(i.Id)).ToList())
            {
                profile.ArchiveSyncItems.Remove(removed);
            }

            // Update matched items and add new ones. RunCount is deliberately not touched.
            foreach (var input in inputs)
            {
                var existing = input.Id != 0
                    ? profile.ArchiveSyncItems.FirstOrDefault(i => i.Id == input.Id)
                    : null;

                if (existing is null)
                {
                    profile.ArchiveSyncItems.Add(NewItem(input));
                }
                else
                {
                    existing.Name = input.Name;
                    existing.SourceFolder = input.SourceFolder;
                    existing.TargetFolder = input.TargetFolder;
                    existing.FileName = input.FileName;
                    existing.IncludeSubFolders = input.IncludeSubFolders;
                    existing.RetentionMode = input.RetentionMode;
                    existing.RetentionCount = input.RetentionCount;
                    existing.MaxLevels = input.MaxLevels;
                }
            }

            return DescribeChanges(oldItems, keptIds, inputs);
        }

        public IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<ArchiveSyncInput> inputs)
        {
            var lines = new List<string>(inputs.Count * 6);
            foreach (var item in inputs)
            {
                lines.Add($"Archive: {item.Name}");
                lines.Add($"Source: {item.SourceFolder}");
                lines.Add($"Target: {item.TargetFolder}");
                lines.Add($"File name: {item.FileName}");
                lines.Add($"Include sub-folders: {YesNo(item.IncludeSubFolders)}");
                lines.Add($"Retention: {RetentionText(item)}");
            }
            return lines;
        }

        private static IReadOnlyList<string> DescribeChanges(
            IReadOnlyDictionary<int, ItemSnapshot> oldItems,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<ArchiveSyncInput> inputs)
        {
            var changes = new List<string>();

            // Removed items: present before, not kept by id now.
            foreach (var (itemId, old) in oldItems)
            {
                if (!keptIds.Contains(itemId))
                {
                    changes.Add($"Archive '{old.Name}' removed");
                }
            }

            // Added or modified items.
            foreach (var input in inputs)
            {
                if (input.Id == 0 || !oldItems.TryGetValue(input.Id, out var old))
                {
                    changes.Add($"Archive '{input.Name}' added ({input.SourceFolder} -> {input.TargetFolder})");
                    continue;
                }

                if (old.Name != input.Name)
                {
                    changes.Add($"Archive '{old.Name}' renamed to '{input.Name}'");
                }
                if (old.SourceFolder != input.SourceFolder)
                {
                    changes.Add($"Archive '{input.Name}' source changed from '{old.SourceFolder}' to '{input.SourceFolder}'");
                }
                if (old.TargetFolder != input.TargetFolder)
                {
                    changes.Add($"Archive '{input.Name}' target changed from '{old.TargetFolder}' to '{input.TargetFolder}'");
                }
                if (old.FileName != input.FileName)
                {
                    changes.Add($"Archive '{input.Name}' file name changed from '{old.FileName}' to '{input.FileName}'");
                }
                if (old.IncludeSubFolders != input.IncludeSubFolders)
                {
                    changes.Add($"Archive '{input.Name}' include sub-folders changed from '{YesNo(old.IncludeSubFolders)}' to '{YesNo(input.IncludeSubFolders)}'");
                }
                if (old.RetentionMode != input.RetentionMode || old.RetentionCount != input.RetentionCount || old.MaxLevels != input.MaxLevels)
                {
                    changes.Add($"Archive '{input.Name}' retention changed from '{RetentionText(old)}' to '{RetentionText(input)}'");
                }
            }

            return changes;
        }

        private static ArchiveSyncItem NewItem(ArchiveSyncInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            FileName = input.FileName,
            IncludeSubFolders = input.IncludeSubFolders,
            RetentionMode = input.RetentionMode,
            RetentionCount = input.RetentionCount,
            MaxLevels = input.MaxLevels,
        };

        private static string RetentionText(ArchiveSyncInput input) =>
            input.RetentionMode == ArchiveRetentionMode.GrandfatherFatherSon
                ? $"{input.RetentionMode.GetDescription()} ({input.RetentionCount} per level, {input.MaxLevels} level(s))"
                : $"{input.RetentionMode.GetDescription()} ({input.RetentionCount})";

        private static string RetentionText(ItemSnapshot snapshot) =>
            snapshot.RetentionMode == ArchiveRetentionMode.GrandfatherFatherSon
                ? $"{snapshot.RetentionMode.GetDescription()} ({snapshot.RetentionCount} per level, {snapshot.MaxLevels} level(s))"
                : $"{snapshot.RetentionMode.GetDescription()} ({snapshot.RetentionCount})";

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private sealed record ItemSnapshot(
            string Name,
            string SourceFolder,
            string TargetFolder,
            string FileName,
            bool IncludeSubFolders,
            ArchiveRetentionMode RetentionMode,
            int RetentionCount,
            int MaxLevels);
    }
}
