using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="ITwoWaySyncItemService"/>. A stateless helper over the tracked
    /// <see cref="Profile"/> entity graph (no DbContext), mirroring <see cref="FolderPairService"/>.
    /// </summary>
    public sealed class TwoWaySyncItemService : ITwoWaySyncItemService
    {
        public void Add(Profile profile, IReadOnlyList<TwoWaySyncInput> inputs)
        {
            foreach (var input in inputs)
            {
                profile.TwoWaySyncItems.Add(NewItem(input));
            }
        }

        public IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<TwoWaySyncInput> inputs)
        {
            var oldItems = profile.TwoWaySyncItems.ToDictionary(
                p => p.Id,
                p => new ItemSnapshot(p.Name, p.SourceFolder, p.TargetFolder, p.IncludeSubFolders, p.ConflictResolution, p.PropagateDeletions, FilterSignature(p.Filters)));

            var keptIds = inputs.Where(f => f.Id != 0).Select(f => f.Id).ToHashSet();
            foreach (var removed in profile.TwoWaySyncItems.Where(p => !keptIds.Contains(p.Id)).ToList())
            {
                profile.TwoWaySyncItems.Remove(removed);
            }

            foreach (var input in inputs)
            {
                var existing = input.Id != 0
                    ? profile.TwoWaySyncItems.FirstOrDefault(p => p.Id == input.Id)
                    : null;

                if (existing is null)
                {
                    profile.TwoWaySyncItems.Add(NewItem(input));
                }
                else
                {
                    existing.Name = input.Name;
                    existing.SourceFolder = input.SourceFolder;
                    existing.TargetFolder = input.TargetFolder;
                    existing.IncludeSubFolders = input.IncludeSubFolders;
                    existing.ConflictResolution = input.ConflictResolution;
                    existing.PropagateDeletions = input.PropagateDeletions;
                    SyncFilters(existing.Filters, input.Filters);
                }
            }

            return DescribeChanges(oldItems, keptIds, inputs);
        }

        public IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<TwoWaySyncInput> inputs)
        {
            var lines = new List<string>(inputs.Count * 6);
            foreach (var item in inputs)
            {
                lines.Add($"Two way sync: {item.Name}");
                lines.Add($"Source: {item.SourceFolder}");
                lines.Add($"Target: {item.TargetFolder}");
                lines.Add($"Include sub-folders: {YesNo(item.IncludeSubFolders)}");
                lines.Add($"Conflict resolution: {item.ConflictResolution.GetDescription()}");
                lines.Add($"Propagate deletions: {YesNo(item.PropagateDeletions)}");
                lines.AddRange(DescribeFilters(item.Filters));
            }
            return lines;
        }

        private static IReadOnlyList<string> DescribeChanges(
            IReadOnlyDictionary<int, ItemSnapshot> oldItems,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<TwoWaySyncInput> inputs)
        {
            var changes = new List<string>();

            foreach (var (itemId, old) in oldItems)
            {
                if (!keptIds.Contains(itemId))
                {
                    changes.Add($"Two way sync '{old.Name}' removed");
                }
            }

            foreach (var input in inputs)
            {
                if (input.Id == 0 || !oldItems.TryGetValue(input.Id, out var old))
                {
                    changes.Add($"Two way sync '{input.Name}' added ({input.SourceFolder} <-> {input.TargetFolder})");
                    continue;
                }

                if (old.Name != input.Name)
                {
                    changes.Add($"Two way sync '{old.Name}' renamed to '{input.Name}'");
                }
                if (old.SourceFolder != input.SourceFolder)
                {
                    changes.Add($"Two way sync '{input.Name}' source changed from '{old.SourceFolder}' to '{input.SourceFolder}'");
                }
                if (old.TargetFolder != input.TargetFolder)
                {
                    changes.Add($"Two way sync '{input.Name}' target changed from '{old.TargetFolder}' to '{input.TargetFolder}'");
                }
                if (old.IncludeSubFolders != input.IncludeSubFolders)
                {
                    changes.Add($"Two way sync '{input.Name}' include sub-folders changed from '{YesNo(old.IncludeSubFolders)}' to '{YesNo(input.IncludeSubFolders)}'");
                }
                if (old.ConflictResolution != input.ConflictResolution)
                {
                    changes.Add($"Two way sync '{input.Name}' conflict resolution changed from '{old.ConflictResolution.GetDescription()}' to '{input.ConflictResolution.GetDescription()}'");
                }
                if (old.PropagateDeletions != input.PropagateDeletions)
                {
                    changes.Add($"Two way sync '{input.Name}' propagate deletions changed from '{YesNo(old.PropagateDeletions)}' to '{YesNo(input.PropagateDeletions)}'");
                }
                if (!old.Filters.SetEquals(FilterSignature(input.Filters)))
                {
                    changes.Add($"Two way sync '{input.Name}' filters changed ({FilterSummary(input.Filters)})");
                }
            }

            return changes;
        }

        private static TwoWaySyncItem NewItem(TwoWaySyncInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            IncludeSubFolders = input.IncludeSubFolders,
            ConflictResolution = input.ConflictResolution,
            PropagateDeletions = input.PropagateDeletions,
            Filters = NewFilters(input.Filters),
        };

        private static List<TwoWaySyncFilter> NewFilters(IReadOnlyList<FilterInput>? inputs) =>
            (inputs ?? []).Select(f => new TwoWaySyncFilter
            {
                Direction = f.Direction,
                Kind = f.Kind,
                Pattern = f.Pattern,
            }).ToList();

        private static void SyncFilters(ICollection<TwoWaySyncFilter> existing, IReadOnlyList<FilterInput>? inputs)
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
                    existing.Add(new TwoWaySyncFilter { Direction = input.Direction, Kind = input.Kind, Pattern = input.Pattern });
                }
                else
                {
                    match.Direction = input.Direction;
                    match.Kind = input.Kind;
                    match.Pattern = input.Pattern;
                }
            }
        }

        private static IReadOnlyList<string> DescribeFilters(IReadOnlyList<FilterInput>? inputs) =>
            (inputs ?? []).Select(FilterLine).ToList();

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

        private static HashSet<string> FilterSignature(IEnumerable<TwoWaySyncFilter> filters) =>
            filters.Select(f => $"{f.Direction}:{f.Kind}:{f.Pattern}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static HashSet<string> FilterSignature(IReadOnlyList<FilterInput>? inputs) =>
            (inputs ?? []).Select(f => $"{f.Direction}:{f.Kind}:{f.Pattern}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private sealed record ItemSnapshot(
            string Name,
            string SourceFolder,
            string TargetFolder,
            bool IncludeSubFolders,
            ConflictResolution ConflictResolution,
            bool PropagateDeletions,
            HashSet<string> Filters);
    }
}
