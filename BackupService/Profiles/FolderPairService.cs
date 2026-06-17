using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IFolderPairService"/>. A stateless helper over the tracked
    /// <see cref="Profile"/> entity graph (no DbContext) — see the interface for why the data
    /// logic lives here rather than in <see cref="ProfileService"/>.
    /// </summary>
    public sealed class FolderPairService : IFolderPairService
    {
        public void Add(Profile profile, IReadOnlyList<FolderPairInput> inputs)
        {
            foreach (var input in inputs)
            {
                profile.FolderPairs.Add(NewFolderPair(input));
            }
        }

        public IReadOnlyList<string> Sync(Profile profile, IReadOnlyList<FolderPairInput> inputs)
        {
            // Snapshot the original pairs before mutating so we can describe what changed.
            var oldPairs = profile.FolderPairs.ToDictionary(
                p => p.Id,
                p => new FolderPairSnapshot(p.Name, p.SourceFolder, p.TargetFolder, p.AllowDeletions, p.IncludeSubFolders, p.OverwriteBehaviour));

            // Remove pairs the user deleted (not present by id in the new set).
            var keptIds = inputs.Where(f => f.Id != 0).Select(f => f.Id).ToHashSet();
            foreach (var removed in profile.FolderPairs.Where(p => !keptIds.Contains(p.Id)).ToList())
            {
                profile.FolderPairs.Remove(removed);
            }

            // Update matched pairs and add new ones.
            foreach (var input in inputs)
            {
                var existing = input.Id != 0
                    ? profile.FolderPairs.FirstOrDefault(p => p.Id == input.Id)
                    : null;

                if (existing is null)
                {
                    profile.FolderPairs.Add(NewFolderPair(input));
                }
                else
                {
                    existing.Name = input.Name;
                    existing.SourceFolder = input.SourceFolder;
                    existing.TargetFolder = input.TargetFolder;
                    existing.AllowDeletions = input.AllowDeletions;
                    existing.IncludeSubFolders = input.IncludeSubFolders;
                    existing.OverwriteBehaviour = input.OverwriteBehaviour;
                }
            }

            return DescribeChanges(oldPairs, keptIds, inputs);
        }

        public IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<FolderPairInput> inputs)
        {
            var lines = new List<string>(inputs.Count * 5);
            foreach (var pair in inputs)
            {
                lines.Add($"Folder pair: {pair.Name}");
                lines.Add($"Source: {pair.SourceFolder}");
                lines.Add($"Target: {pair.TargetFolder}");
                lines.Add($"Allow deletions: {YesNo(pair.AllowDeletions)}");
                lines.Add($"Include sub-folders: {YesNo(pair.IncludeSubFolders)}");
                lines.Add($"Overwrite: {pair.OverwriteBehaviour.GetDescription()}");
            }
            return lines;
        }

        private static IReadOnlyList<string> DescribeChanges(
            IReadOnlyDictionary<int, FolderPairSnapshot> oldPairs,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<FolderPairInput> inputs)
        {
            var changes = new List<string>();

            // Removed folder pairs: present before, not kept by id now.
            foreach (var (pairId, old) in oldPairs)
            {
                if (!keptIds.Contains(pairId))
                {
                    changes.Add($"Folder pair '{old.Name}' removed");
                }
            }

            // Added or modified folder pairs.
            foreach (var input in inputs)
            {
                if (input.Id == 0 || !oldPairs.TryGetValue(input.Id, out var old))
                {
                    changes.Add($"Folder pair '{input.Name}' added ({input.SourceFolder} -> {input.TargetFolder})");
                    continue;
                }

                if (old.Name != input.Name)
                {
                    changes.Add($"Folder pair '{old.Name}' renamed to '{input.Name}'");
                }
                if (old.SourceFolder != input.SourceFolder)
                {
                    changes.Add($"Folder pair '{input.Name}' source changed from '{old.SourceFolder}' to '{input.SourceFolder}'");
                }
                if (old.TargetFolder != input.TargetFolder)
                {
                    changes.Add($"Folder pair '{input.Name}' target changed from '{old.TargetFolder}' to '{input.TargetFolder}'");
                }
                if (old.AllowDeletions != input.AllowDeletions)
                {
                    changes.Add($"Folder pair '{input.Name}' allow deletions changed from '{YesNo(old.AllowDeletions)}' to '{YesNo(input.AllowDeletions)}'");
                }
                if (old.IncludeSubFolders != input.IncludeSubFolders)
                {
                    changes.Add($"Folder pair '{input.Name}' include sub-folders changed from '{YesNo(old.IncludeSubFolders)}' to '{YesNo(input.IncludeSubFolders)}'");
                }
                if (old.OverwriteBehaviour != input.OverwriteBehaviour)
                {
                    changes.Add($"Folder pair '{input.Name}' overwrite changed from '{old.OverwriteBehaviour.GetDescription()}' to '{input.OverwriteBehaviour.GetDescription()}'");
                }
            }

            return changes;
        }

        private static FolderPair NewFolderPair(FolderPairInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            AllowDeletions = input.AllowDeletions,
            IncludeSubFolders = input.IncludeSubFolders,
            OverwriteBehaviour = input.OverwriteBehaviour,
            Status = FolderPairStatus.Idle,
            LastRunStatus = FolderPairLastRunStatus.None,
        };

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private sealed record FolderPairSnapshot(
            string Name,
            string SourceFolder,
            string TargetFolder,
            bool AllowDeletions,
            bool IncludeSubFolders,
            OverwriteBehaviour OverwriteBehaviour);
    }
}
