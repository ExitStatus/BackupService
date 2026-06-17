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
        IOperationLogFactory operationLogFactory,
        IFolderPairService folderPairService,
        IBackupScheduler scheduler,
        IProfileStatusService statusService) : IProfileService
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
            };

            folderPairService.Add(profile, folderPairs);

            db.Profiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);

            await LogProfileCreatedAsync(profile.Id, name, description, type, scheduleCron, enabled, folderPairs, cancellationToken);

            // Track the new profile's status (starts Idle) and register its schedule.
            statusService.Set(profile.Id, ProfileStatus.Idle);
            await scheduler.SyncAsync(profile.Id, cancellationToken);
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

            var pairLines = folderPairService.DescribeForCreateLog(folderPairs);
            if (pairLines.Count > 0)
            {
                await log.AppendAsync(pairLines.ToArray());
            }
        }

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

            // The row is gone, so this unschedules the profile and drops its tracked status.
            statusService.Remove(id);
            await scheduler.SyncAsync(id, cancellationToken);
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

            await scheduler.SyncAsync(id, cancellationToken);
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

            // Snapshot the original profile-level values so we can log what changed after saving.
            var oldName = profile.Name;
            var oldDescription = profile.Description;
            var oldSchedule = profile.Schedule;
            var oldEnabled = profile.Enabled;

            profile.Name = name;
            profile.Description = description;
            profile.Schedule = scheduleCron;
            profile.Enabled = enabled;

            // The folder-pair data (and the description of what changed within it) is owned by
            // the folder-pair service.
            var folderPairChanges = folderPairService.Sync(profile, folderPairs);

            await db.SaveChangesAsync(cancellationToken);

            await LogProfileUpdatedAsync(
                id, oldName, name, oldDescription, description, oldSchedule, scheduleCron,
                oldEnabled, enabled, folderPairChanges, cancellationToken);

            await scheduler.SyncAsync(id, cancellationToken);
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
            IReadOnlyList<string> folderPairChanges,
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

            // Folder-pair changes are described by the folder-pair service.
            changes.AddRange(folderPairChanges);

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
    }
}
