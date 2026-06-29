using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Security;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IArchiveSyncItemService"/>. A stateless helper over the tracked
    /// <see cref="Profile"/> entity graph (no DbContext), mirroring <see cref="FolderPairService"/> and
    /// <see cref="InstantSyncItemService"/>. The per-item <c>RunCount</c> (which drives the GFS
    /// promotion cadence) is owned by the run, so it is left untouched here. Archive passwords are
    /// encrypted at rest via <see cref="ISecretProtector"/> and never logged.
    /// </summary>
    public sealed class ArchiveSyncItemService(ISecretProtector secretProtector) : IArchiveSyncItemService
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
                i => new ItemSnapshot(i.Name, i.SourceFolder, i.TargetFolder, i.FileName, i.IncludeSubFolders, i.OnlyCopyOnChange, i.CompressionLevel, i.PasswordProtect, i.EncryptionMethod, i.RetentionMode, i.RetentionCount, i.MaxLevels, FilterSignature(i.Filters)));

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
                    existing.OnlyCopyOnChange = input.OnlyCopyOnChange;
                    existing.CompressionLevel = input.CompressionLevel;
                    existing.PasswordProtect = input.PasswordProtect;
                    existing.EncryptionMethod = input.EncryptionMethod;
                    ApplyPassword(existing, input);
                    existing.RetentionMode = input.RetentionMode;
                    existing.RetentionCount = input.RetentionCount;
                    existing.MaxLevels = input.MaxLevels;
                    SyncFilters(existing.Filters, input.Filters);
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
                lines.Add($"Only copy on change: {YesNo(item.OnlyCopyOnChange)}");
                lines.Add($"Compression: {item.CompressionLevel.GetDescription()}");
                lines.Add($"Password protected: {YesNo(item.PasswordProtect)}");
                if (item.PasswordProtect)
                {
                    lines.Add($"Encryption: {item.EncryptionMethod.GetDescription()}");
                }
                lines.Add($"Retention: {RetentionText(item)}");
                lines.AddRange((item.Filters ?? []).Select(FilterLine));
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
                if (old.OnlyCopyOnChange != input.OnlyCopyOnChange)
                {
                    changes.Add($"Archive '{input.Name}' only-copy-on-change changed from '{YesNo(old.OnlyCopyOnChange)}' to '{YesNo(input.OnlyCopyOnChange)}'");
                }
                if (old.CompressionLevel != input.CompressionLevel)
                {
                    changes.Add($"Archive '{input.Name}' compression changed from '{old.CompressionLevel.GetDescription()}' to '{input.CompressionLevel.GetDescription()}'");
                }
                if (old.PasswordProtect != input.PasswordProtect)
                {
                    changes.Add($"Archive '{input.Name}' password protection changed from '{YesNo(old.PasswordProtect)}' to '{YesNo(input.PasswordProtect)}'");
                }
                else if (input.PasswordProtect && !string.IsNullOrEmpty(input.Password))
                {
                    changes.Add($"Archive '{input.Name}' password updated");
                }
                if (input.PasswordProtect && old.EncryptionMethod != input.EncryptionMethod)
                {
                    changes.Add($"Archive '{input.Name}' encryption changed from '{old.EncryptionMethod.GetDescription()}' to '{input.EncryptionMethod.GetDescription()}'");
                }
                if (old.RetentionMode != input.RetentionMode || old.RetentionCount != input.RetentionCount || old.MaxLevels != input.MaxLevels)
                {
                    changes.Add($"Archive '{input.Name}' retention changed from '{RetentionText(old)}' to '{RetentionText(input)}'");
                }
                if (!old.Filters.SetEquals(FilterSignature(input.Filters)))
                {
                    changes.Add($"Archive '{input.Name}' filters changed ({FilterSummary(input.Filters)})");
                }
            }

            return changes;
        }

        private ArchiveSyncItem NewItem(ArchiveSyncInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            FileName = input.FileName,
            IncludeSubFolders = input.IncludeSubFolders,
            OnlyCopyOnChange = input.OnlyCopyOnChange,
            CompressionLevel = input.CompressionLevel,
            PasswordProtect = input.PasswordProtect,
            EncryptionMethod = input.EncryptionMethod,
            PasswordEncrypted = input.PasswordProtect && !string.IsNullOrEmpty(input.Password)
                ? secretProtector.Protect(input.Password)
                : null,
            RetentionMode = input.RetentionMode,
            RetentionCount = input.RetentionCount,
            MaxLevels = input.MaxLevels,
            Filters = NewFilters(input.Filters),
        };

        // Apply the password on update: drop the stored secret when protection is off, re-encrypt when a new
        // password was typed, otherwise keep the existing stored one (a blank box means "keep").
        private void ApplyPassword(ArchiveSyncItem existing, ArchiveSyncInput input)
        {
            if (!input.PasswordProtect)
            {
                existing.PasswordEncrypted = null;
            }
            else if (!string.IsNullOrEmpty(input.Password))
            {
                existing.PasswordEncrypted = secretProtector.Protect(input.Password);
            }
        }

        private static List<ArchiveSyncFilter> NewFilters(IReadOnlyList<FilterInput>? inputs) =>
            (inputs ?? []).Select(f => new ArchiveSyncFilter
            {
                Direction = f.Direction,
                Kind = f.Kind,
                Pattern = f.Pattern,
            }).ToList();

        // Reconcile a tracked item's filter rows against the input: update matched by id, add id-0, drop the rest.
        private static void SyncFilters(ICollection<ArchiveSyncFilter> existing, IReadOnlyList<FilterInput>? inputs)
        {
            inputs ??= [];
            var keptIds = inputs.Where(f => f.Id != 0).Select(f => f.Id).ToHashSet();
            foreach (var removed in existing.Where(f => !keptIds.Contains(f.Id)).ToList())
            {
                existing.Remove(removed);
            }

            foreach (var input in inputs)
            {
                var match = input.Id != 0 ? existing.FirstOrDefault(f => f.Id == input.Id) : null;
                if (match is null)
                {
                    existing.Add(new ArchiveSyncFilter { Direction = input.Direction, Kind = input.Kind, Pattern = input.Pattern });
                }
                else
                {
                    match.Direction = input.Direction;
                    match.Kind = input.Kind;
                    match.Pattern = input.Pattern;
                }
            }
        }

        private static string FilterLine(FilterInput f) =>
            f.Direction == FilterDirection.Include
                ? $"Include: {f.Pattern}"
                : $"Exclude {f.Kind.GetDescription().ToLowerInvariant()}: {f.Pattern}";

        private static string FilterSummary(IReadOnlyList<FilterInput>? inputs)
        {
            inputs ??= [];
            var includes = inputs.Count(f => f.Direction == FilterDirection.Include);
            var excludes = inputs.Count - includes;
            return $"{includes} include(s), {excludes} exclude(s)";
        }

        private static HashSet<string> FilterSignature(IEnumerable<ArchiveSyncFilter> filters) =>
            filters.Select(f => $"{f.Direction}:{f.Kind}:{f.Pattern}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> FilterSignature(IReadOnlyList<FilterInput>? inputs) =>
            (inputs ?? []).Select(f => $"{f.Direction}:{f.Kind}:{f.Pattern}").ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            bool OnlyCopyOnChange,
            ArchiveCompressionLevel CompressionLevel,
            bool PasswordProtect,
            ArchiveEncryptionMethod EncryptionMethod,
            ArchiveRetentionMode RetentionMode,
            int RetentionCount,
            int MaxLevels,
            HashSet<string> Filters);
    }
}
