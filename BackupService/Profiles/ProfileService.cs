using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IProfileService"/>. Persists via the DbContext factory (a
    /// short-lived context per call, per the project's factory convention).
    /// </summary>
    public sealed class ProfileService(
        IDatabaseContextFactory contextFactory,
        IOperationLogFactory operationLogFactory) : IProfileService
    {
        public async Task CreateAsync(
            string name,
            string? description,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var profile = new Profile
            {
                Name = name,
                Description = description,
                Type = type,
                Schedule = scheduleCron,
                Enabled = enabled,
                DateCreated = DateTimeOffset.UtcNow,
                Status = ProfileStatus.Idle,
            };

            foreach (var input in folderPairs)
            {
                profile.FolderPairs.Add(NewFolderPair(input));
            }

            db.Profiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);

            await LogProfileCreatedAsync(profile.Id, name, description, type, scheduleCron, enabled, folderPairs, cancellationToken);
        }

        private async Task LogProfileCreatedAsync(
            int profileId,
            string name,
            string? description,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken)
        {
            var log = await operationLogFactory.CreateAsync($"Profile created: {name}", profileId: profileId, cancellationToken: cancellationToken);

            await log.AppendAsync(
                $"Name: {name}",
                $"Description: {DisplayText(description)}",
                $"Type: {type.GetDescription()}",
                $"Schedule: {ScheduleDefinition.Describe(scheduleCron)}",
                $"Enabled: {YesNo(enabled)}");

            foreach (var pair in folderPairs)
            {
                await log.AppendAsync(
                    $"Folder pair: {pair.Name}",
                    $"Source: {pair.SourceFolder}",
                    $"Target: {pair.TargetFolder}",
                    $"Watch: {YesNo(pair.WatchFolder)}",
                    $"Allow deletions: {YesNo(pair.AllowDeletions)}",
                    $"Overwrite: {pair.OverwriteBehaviour.GetDescription()}");
            }
        }

        private static FolderPair NewFolderPair(FolderPairInput input) => new()
        {
            Name = input.Name,
            SourceFolder = input.SourceFolder,
            TargetFolder = input.TargetFolder,
            WatchFolder = input.WatchFolder,
            AllowDeletions = input.AllowDeletions,
            OverwriteBehaviour = input.OverwriteBehaviour,
            Status = FolderPairStatus.Idle,
            LastRunStatus = FolderPairLastRunStatus.None,
        };

        public async Task<PagedResult<Profile>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ProfileSortColumn sortColumn,
            bool descending,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber < 1)
            {
                pageNumber = 1;
            }
            if (pageSize < 1)
            {
                pageSize = 1;
            }

            await using var db = contextFactory.CreateDbContext();

            var query = db.Profiles.AsNoTracking();
            var totalCount = await query.CountAsync(cancellationToken);
            var skip = (pageNumber - 1) * pageSize;

            IReadOnlyList<Profile> items;
            if (sortColumn == ProfileSortColumn.DateLastRun)
            {
                // SQLite cannot ORDER BY a DateTimeOffset column, so sort this column in
                // memory. The profile count is small (an admin-managed local list).
                var all = await query.ToListAsync(cancellationToken);
                var ordered = descending
                    ? all.OrderByDescending(p => p.DateLastRun)
                    : all.OrderBy(p => p.DateLastRun);
                items = ordered.Skip(skip).Take(pageSize).ToList();
            }
            else
            {
                IQueryable<Profile> ordered = sortColumn switch
                {
                    ProfileSortColumn.Description => descending
                        ? query.OrderByDescending(p => p.Description)
                        : query.OrderBy(p => p.Description),
                    ProfileSortColumn.Status => descending
                        ? query.OrderByDescending(p => p.Status)
                        : query.OrderBy(p => p.Status),
                    _ => descending
                        ? query.OrderByDescending(p => p.Name)
                        : query.OrderBy(p => p.Name),
                };
                items = await ordered.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
            }

            return new PagedResult<Profile>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Profile?> GetAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .Include(p => p.FolderPairs)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        }

        public async Task<IReadOnlyList<ProfileSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .OrderBy(p => p.Name)
                .Select(p => new ProfileSummary(p.Id, p.Name))
                .ToListAsync(cancellationToken);
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (profile is null)
            {
                return;
            }

            // Capture the details before the row is removed so we can log what was deleted.
            var name = profile.Name;
            var description = profile.Description;
            var type = profile.Type;

            db.Profiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);

            // Not associated with the profile (it's gone, and that association would cascade-delete
            // this very record) — the deletion log is meant to survive.
            var log = await operationLogFactory.CreateAsync($"Profile deleted: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync(
                $"Name: {name}",
                $"Description: {DisplayText(description)}",
                $"Type: {type.GetDescription()}");
        }

        public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (profile is null)
            {
                return;
            }

            profile.Enabled = enabled;
            await db.SaveChangesAsync(cancellationToken);

            // Simple, self-describing event — the message lives in the name, no detail lines.
            await operationLogFactory.CreateAsync(
                $"Profile {profile.Name} was {(enabled ? "enabled" : "disabled")}",
                profileId: profile.Id,
                cancellationToken: cancellationToken);
        }

        public async Task UpdateAsync(
            int id,
            string name,
            string? description,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var profile = await db.Profiles
                .Include(p => p.FolderPairs)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (profile is null)
            {
                return;
            }

            // Snapshot the original values so we can log what changed after saving.
            var oldName = profile.Name;
            var oldDescription = profile.Description;
            var oldSchedule = profile.Schedule;
            var oldEnabled = profile.Enabled;
            var oldPairs = profile.FolderPairs.ToDictionary(
                p => p.Id,
                p => new FolderPairSnapshot(p.Name, p.SourceFolder, p.TargetFolder, p.WatchFolder, p.AllowDeletions, p.OverwriteBehaviour));

            profile.Name = name;
            profile.Description = description;
            profile.Schedule = scheduleCron;
            profile.Enabled = enabled;

            // Remove pairs the user deleted (not present by id in the new set).
            var keptIds = folderPairs.Where(f => f.Id != 0).Select(f => f.Id).ToHashSet();
            foreach (var removed in profile.FolderPairs.Where(p => !keptIds.Contains(p.Id)).ToList())
            {
                profile.FolderPairs.Remove(removed);
            }

            // Update matched pairs and add new ones.
            foreach (var input in folderPairs)
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
                    existing.WatchFolder = input.WatchFolder;
                    existing.AllowDeletions = input.AllowDeletions;
                    existing.OverwriteBehaviour = input.OverwriteBehaviour;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            await LogProfileUpdatedAsync(
                id, oldName, name, oldDescription, description, oldSchedule, scheduleCron,
                oldEnabled, enabled, oldPairs, keptIds, folderPairs, cancellationToken);
        }

        private async Task LogProfileUpdatedAsync(
            int profileId,
            string oldName,
            string newName,
            string? oldDescription,
            string? newDescription,
            string? oldSchedule,
            string? newSchedule,
            bool oldEnabled,
            bool newEnabled,
            IReadOnlyDictionary<int, FolderPairSnapshot> oldPairs,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<FolderPairInput> folderPairs,
            CancellationToken cancellationToken)
        {
            var changes = new List<string>();

            if (oldName != newName)
            {
                changes.Add($"Name changed from '{oldName}' to '{newName}'");
            }
            if (oldDescription != newDescription)
            {
                changes.Add($"Description changed from '{DisplayText(oldDescription)}' to '{DisplayText(newDescription)}'");
            }
            if (oldSchedule != newSchedule)
            {
                changes.Add($"Schedule changed from '{ScheduleDefinition.Describe(oldSchedule)}' to '{ScheduleDefinition.Describe(newSchedule)}'");
            }
            if (oldEnabled != newEnabled)
            {
                changes.Add($"Enabled changed from '{YesNo(oldEnabled)}' to '{YesNo(newEnabled)}'");
            }

            // Removed folder pairs: present before, not kept by id now.
            foreach (var (pairId, old) in oldPairs)
            {
                if (!keptIds.Contains(pairId))
                {
                    changes.Add($"Folder pair '{old.Name}' removed");
                }
            }

            // Added or modified folder pairs.
            foreach (var input in folderPairs)
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
                if (old.WatchFolder != input.WatchFolder)
                {
                    changes.Add($"Folder pair '{input.Name}' watch changed from '{YesNo(old.WatchFolder)}' to '{YesNo(input.WatchFolder)}'");
                }
                if (old.AllowDeletions != input.AllowDeletions)
                {
                    changes.Add($"Folder pair '{input.Name}' allow deletions changed from '{YesNo(old.AllowDeletions)}' to '{YesNo(input.AllowDeletions)}'");
                }
                if (old.OverwriteBehaviour != input.OverwriteBehaviour)
                {
                    changes.Add($"Folder pair '{input.Name}' overwrite changed from '{old.OverwriteBehaviour.GetDescription()}' to '{input.OverwriteBehaviour.GetDescription()}'");
                }
            }

            var log = await operationLogFactory.CreateAsync($"Profile updated: {oldName}", profileId: profileId, cancellationToken: cancellationToken);

            if (changes.Count == 0)
            {
                await log.AppendAsync("No changes detected.");
                return;
            }

            await log.AppendAsync(changes.ToArray());
        }

        private static string DisplayText(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "(none)" : value;

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private sealed record FolderPairSnapshot(
            string Name,
            string SourceFolder,
            string TargetFolder,
            bool WatchFolder,
            bool AllowDeletions,
            OverwriteBehaviour OverwriteBehaviour);
    }
}
