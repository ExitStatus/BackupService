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
        IInstantSyncItemService instantSyncItemService,
        IArchiveSyncItemService archiveSyncItemService,
        ILightroomArchiveItemService lightroomArchiveItemService,
        IBackupScheduler scheduler,
        IInstantSyncManager instantSyncManager,
        ILightroomArchiveManager lightroomArchiveManager,
        IProfileStatusService statusService) : IProfileService
    {
        public async Task CreateAsync(
            string name,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            IReadOnlyList<InstantSyncInput>? instantSyncItems = null,
            IReadOnlyList<ArchiveSyncInput>? archiveSyncItems = null,
            IReadOnlyList<LightroomArchiveInput>? lightroomArchiveItems = null,
            string? lightroomFolder = null,
            string? rawFormats = null,
            string? rawFolderName = null,
            bool handleMissedSync = false,
            int? sourceConnectionId = null,
            int? targetConnectionId = null,
            bool notificationsEnabled = true,
            bool notifyOnStart = false,
            bool notifyOnComplete = true,
            bool notifyOnEject = false,
            bool showProgressWindow = false,
            bool ejectAfterRun = false,
            CancellationToken cancellationToken = default)
        {
            var instantItems = instantSyncItems ?? [];
            var archiveItems = archiveSyncItems ?? [];
            var lightroomItems = lightroomArchiveItems ?? [];

            await using var db = contextFactory.CreateDbContext();

            var profile = new Profile
            {
                Name = name,
                Type = type,
                Schedule = scheduleCron,
                Enabled = enabled,
                HandleMissedSync = handleMissedSync,
                SourceConnectionId = sourceConnectionId,
                TargetConnectionId = targetConnectionId,
                NotificationsEnabled = notificationsEnabled,
                NotifyOnStart = notifyOnStart,
                NotifyOnComplete = notifyOnComplete,
                NotifyOnEject = notifyOnEject,
                ShowProgressWindow = showProgressWindow,
                EjectAfterRun = ejectAfterRun,
                DateCreated = DateTimeOffset.UtcNow,
            };

            // Only the data for the profile's own type is applied; the other lists stay empty.
            switch (type)
            {
                case ProfileType.InstantSync:
                    instantSyncItemService.Add(profile, instantItems);
                    break;
                case ProfileType.ArchiveSync:
                    archiveSyncItemService.Add(profile, archiveItems);
                    break;
                case ProfileType.LightroomArchive:
                    ApplyLightroomSettings(profile, lightroomFolder, rawFormats, rawFolderName);
                    lightroomArchiveItemService.Add(profile, lightroomItems);
                    break;
                default:
                    folderPairService.Add(profile, folderPairs);
                    break;
            }

            db.Profiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);

            var sourceLabel = await ConnectionLabelAsync(db, sourceConnectionId, cancellationToken);
            var targetLabel = await ConnectionLabelAsync(db, targetConnectionId, cancellationToken);
            await LogProfileCreatedAsync(profile.Id, name, type, scheduleCron, enabled, handleMissedSync, sourceLabel, targetLabel,
                notificationsEnabled, notifyOnStart, notifyOnComplete, notifyOnEject, showProgressWindow, folderPairs, instantItems, archiveItems, lightroomItems, cancellationToken);

            // Track the new profile's status (starts Idle), then register it with all drivers — the
            // scheduler (cron-driven types) and the watcher managers (watcher-driven types). Each is a
            // no-op for the wrong type.
            statusService.Set(profile.Id, ProfileStatus.Idle);
            await scheduler.SyncAsync(profile.Id, cancellationToken);
            await instantSyncManager.SyncAsync(profile.Id, cancellationToken);
            await lightroomArchiveManager.SyncAsync(profile.Id, cancellationToken);
        }

        // Sets the profile-level LightroomArchive settings (the per-item data is owned by the item service).
        private static void ApplyLightroomSettings(Profile profile, string? lightroomFolder, string? rawFormats, string? rawFolderName)
        {
            profile.LightroomFolder = lightroomFolder;
            profile.RawFormats = rawFormats;
            profile.RawFolderName = rawFolderName;
        }

        // The display label for a (profile-level) connection: the connection name, or "this machine (local)" when null.
        private static async Task<string> ConnectionLabelAsync(BackupDbContext db, int? connectionId, CancellationToken cancellationToken)
        {
            if (connectionId is not { } id)
            {
                return "this machine (local)";
            }
            var name = await db.Connections.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken);
            return name ?? $"connection #{id}";
        }

        private async Task LogProfileCreatedAsync(
            int profileId,
            string name,
            ProfileType type,
            string? scheduleCron,
            bool enabled,
            bool handleMissedSync,
            string sourceConnectionLabel,
            string targetConnectionLabel,
            bool notificationsEnabled,
            bool notifyOnStart,
            bool notifyOnComplete,
            bool notifyOnEject,
            bool showProgressWindow,
            IReadOnlyList<FolderPairInput> folderPairs,
            IReadOnlyList<InstantSyncInput> instantSyncItems,
            IReadOnlyList<ArchiveSyncInput> archiveSyncItems,
            IReadOnlyList<LightroomArchiveInput> lightroomArchiveItems,
            CancellationToken cancellationToken)
        {
            var log = await operationLogFactory.CreateAsync($"Profile created: {name}", profileId: profileId, cancellationToken: cancellationToken);

            await log.AppendAsync(
                $"Name: {name}",
                $"Type: {type.GetDescription()}",
                $"Source connection: {sourceConnectionLabel}",
                $"Target connection: {targetConnectionLabel}",
                $"Schedule: {ScheduleDefinition.Describe(scheduleCron)}",
                $"Handle missed sync: {YesNo(handleMissedSync)}",
                $"Notifications: {NotificationsSummary(notificationsEnabled, notifyOnStart, notifyOnComplete, notifyOnEject, showProgressWindow)}",
                $"Enabled: {YesNo(enabled)}");

            var itemLines = type switch
            {
                ProfileType.InstantSync => instantSyncItemService.DescribeForCreateLog(instantSyncItems),
                ProfileType.ArchiveSync => archiveSyncItemService.DescribeForCreateLog(archiveSyncItems),
                ProfileType.LightroomArchive => lightroomArchiveItemService.DescribeForCreateLog(lightroomArchiveItems),
                _ => folderPairService.DescribeForCreateLog(folderPairs),
            };
            if (itemLines.Count > 0)
            {
                await log.AppendAsync(itemLines.ToArray());
            }
        }

        public async Task<PagedResult<Profile>> GetPageAsync(
            int pageNumber,
            int pageSize,
            ProfileSortColumn sortColumn,
            bool descending,
            ProfileType? type = null,
            string? filter = null,
            bool? enabled = null,
            int? connectionId = null,
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
            if (type is { } t)
            {
                query = query.Where(p => p.Type == t);
            }
            if (enabled is { } en)
            {
                query = query.Where(p => p.Enabled == en);
            }
            if (connectionId is { } cid)
            {
                // A profile "uses" a connection as its source or its target.
                query = query.Where(p => p.SourceConnectionId == cid || p.TargetConnectionId == cid);
            }
            if (!string.IsNullOrWhiteSpace(filter))
            {
                // LIKE is case-insensitive for ASCII in SQLite; matches the name.
                var like = $"%{filter.Trim()}%";
                query = query.Where(p => EF.Functions.Like(p.Name, like));
            }
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
                IQueryable<Profile> ordered = descending
                    ? query.OrderByDescending(p => p.Name)
                    : query.OrderBy(p => p.Name);
                items = await ordered.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
            }

            return new PagedResult<Profile>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Profile?> GetAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .Include(p => p.FolderPairs).ThenInclude(fp => fp.Filters)
                .Include(p => p.InstantSyncItems)
                .Include(p => p.ArchiveSyncItems).ThenInclude(a => a.Filters)
                .Include(p => p.LightroomArchiveItems)
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

        public async Task<IReadOnlyDictionary<ProfileType, int>> GetCountsByTypeAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var counts = await db.Profiles
                .AsNoTracking()
                .GroupBy(p => p.Type)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return counts.ToDictionary(c => c.Type, c => c.Count);
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
            var type = profile.Type;

            db.Profiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);

            // Not associated with the profile (it's gone, and that association would cascade-delete
            // this very record) — the deletion log is meant to survive.
            var log = await operationLogFactory.CreateAsync($"Profile deleted: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync(
                $"Name: {name}",
                $"Type: {type.GetDescription()}");

            // The row is gone, so this unschedules the profile, tears down any watchers, and drops
            // its tracked status.
            statusService.Remove(id);
            await scheduler.SyncAsync(id, cancellationToken);
            await instantSyncManager.SyncAsync(id, cancellationToken);
            await lightroomArchiveManager.SyncAsync(id, cancellationToken);
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

            // Enabling/disabling a watcher-driven profile starts/stops its watchers.
            await scheduler.SyncAsync(id, cancellationToken);
            await instantSyncManager.SyncAsync(id, cancellationToken);
            await lightroomArchiveManager.SyncAsync(id, cancellationToken);
        }

        public async Task UpdateAsync(
            int id,
            string name,
            string? scheduleCron,
            bool enabled,
            IReadOnlyList<FolderPairInput> folderPairs,
            IReadOnlyList<InstantSyncInput>? instantSyncItems = null,
            IReadOnlyList<ArchiveSyncInput>? archiveSyncItems = null,
            IReadOnlyList<LightroomArchiveInput>? lightroomArchiveItems = null,
            string? lightroomFolder = null,
            string? rawFormats = null,
            string? rawFolderName = null,
            bool handleMissedSync = false,
            int? sourceConnectionId = null,
            int? targetConnectionId = null,
            bool notificationsEnabled = true,
            bool notifyOnStart = false,
            bool notifyOnComplete = true,
            bool notifyOnEject = false,
            bool showProgressWindow = false,
            bool ejectAfterRun = false,
            CancellationToken cancellationToken = default)
        {
            var instantItems = instantSyncItems ?? [];
            var archiveItems = archiveSyncItems ?? [];
            var lightroomItems = lightroomArchiveItems ?? [];

            await using var db = contextFactory.CreateDbContext();

            var profile = await db.Profiles
                .Include(p => p.FolderPairs).ThenInclude(fp => fp.Filters)
                .Include(p => p.InstantSyncItems)
                .Include(p => p.ArchiveSyncItems).ThenInclude(a => a.Filters)
                .Include(p => p.LightroomArchiveItems)
                .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

            if (profile is null)
            {
                return;
            }

            // Snapshot the original profile-level values so we can log what changed after saving.
            var oldName = profile.Name;
            var oldSchedule = profile.Schedule;
            var oldEnabled = profile.Enabled;
            var oldHandleMissedSync = profile.HandleMissedSync;
            var oldSourceConnectionId = profile.SourceConnectionId;
            var oldTargetConnectionId = profile.TargetConnectionId;
            var oldNotifications = NotificationsSummary(profile.NotificationsEnabled, profile.NotifyOnStart, profile.NotifyOnComplete, profile.NotifyOnEject, profile.ShowProgressWindow);

            profile.Name = name;
            profile.Schedule = scheduleCron;
            profile.Enabled = enabled;
            profile.HandleMissedSync = handleMissedSync;
            profile.SourceConnectionId = sourceConnectionId;
            profile.TargetConnectionId = targetConnectionId;
            profile.NotificationsEnabled = notificationsEnabled;
            profile.NotifyOnStart = notifyOnStart;
            profile.NotifyOnComplete = notifyOnComplete;
            profile.NotifyOnEject = notifyOnEject;
            profile.ShowProgressWindow = showProgressWindow;
            profile.EjectAfterRun = ejectAfterRun;

            // The type-specific item data (and the description of what changed within it) is owned by
            // the matching data service; the profile type is fixed, so only that one runs.
            var itemChanges = profile.Type switch
            {
                ProfileType.InstantSync => instantSyncItemService.Sync(profile, instantItems),
                ProfileType.ArchiveSync => archiveSyncItemService.Sync(profile, archiveItems),
                ProfileType.LightroomArchive => SyncLightroom(profile, lightroomItems, lightroomFolder, rawFormats, rawFolderName),
                _ => folderPairService.Sync(profile, folderPairs),
            };

            await db.SaveChangesAsync(cancellationToken);

            var oldSourceLabel = await ConnectionLabelAsync(db, oldSourceConnectionId, cancellationToken);
            var newSourceLabel = await ConnectionLabelAsync(db, sourceConnectionId, cancellationToken);
            var oldTargetLabel = await ConnectionLabelAsync(db, oldTargetConnectionId, cancellationToken);
            var newTargetLabel = await ConnectionLabelAsync(db, targetConnectionId, cancellationToken);
            await LogProfileUpdatedAsync(
                id, oldName, name, oldSchedule, scheduleCron,
                oldEnabled, enabled, oldHandleMissedSync, handleMissedSync,
                oldSourceConnectionId, sourceConnectionId, oldSourceLabel, newSourceLabel,
                oldTargetConnectionId, targetConnectionId, oldTargetLabel, newTargetLabel,
                oldNotifications, NotificationsSummary(notificationsEnabled, notifyOnStart, notifyOnComplete, notifyOnEject, showProgressWindow),
                itemChanges, cancellationToken);

            await scheduler.SyncAsync(id, cancellationToken);
            await instantSyncManager.SyncAsync(id, cancellationToken);
            await lightroomArchiveManager.SyncAsync(id, cancellationToken);
        }

        // Updates the profile-level Lightroom settings alongside reconciling its items.
        private IReadOnlyList<string> SyncLightroom(Profile profile, IReadOnlyList<LightroomArchiveInput> items, string? lightroomFolder, string? rawFormats, string? rawFolderName)
        {
            ApplyLightroomSettings(profile, lightroomFolder, rawFormats, rawFolderName);
            return lightroomArchiveItemService.Sync(profile, items);
        }

        private async Task LogProfileUpdatedAsync(
            int profileId,
            string oldName,
            string newName,
            string? oldSchedule,
            string? newSchedule,
            bool oldEnabled,
            bool newEnabled,
            bool oldHandleMissedSync,
            bool newHandleMissedSync,
            int? oldSourceConnectionId,
            int? newSourceConnectionId,
            string oldSourceLabel,
            string newSourceLabel,
            int? oldTargetConnectionId,
            int? newTargetConnectionId,
            string oldTargetLabel,
            string newTargetLabel,
            string oldNotifications,
            string newNotifications,
            IReadOnlyList<string> itemChanges,
            CancellationToken cancellationToken)
        {
            var changes = new List<string>();

            if (oldName != newName)
            {
                changes.Add($"Name changed from '{oldName}' to '{newName}'");
            }
            if (oldSourceConnectionId != newSourceConnectionId)
            {
                changes.Add($"Source connection changed from '{oldSourceLabel}' to '{newSourceLabel}'");
            }
            if (oldTargetConnectionId != newTargetConnectionId)
            {
                changes.Add($"Target connection changed from '{oldTargetLabel}' to '{newTargetLabel}'");
            }
            if (oldSchedule != newSchedule)
            {
                changes.Add($"Schedule changed from '{ScheduleDefinition.Describe(oldSchedule)}' to '{ScheduleDefinition.Describe(newSchedule)}'");
            }
            if (oldEnabled != newEnabled)
            {
                changes.Add($"Enabled changed from '{YesNo(oldEnabled)}' to '{YesNo(newEnabled)}'");
            }
            if (oldHandleMissedSync != newHandleMissedSync)
            {
                changes.Add($"Handle missed sync changed from '{YesNo(oldHandleMissedSync)}' to '{YesNo(newHandleMissedSync)}'");
            }
            if (oldNotifications != newNotifications)
            {
                changes.Add($"Notifications changed from '{oldNotifications}' to '{newNotifications}'");
            }

            // Item changes are described by the matching type's data service.
            changes.AddRange(itemChanges);

            var log = await operationLogFactory.CreateAsync($"Profile updated: {oldName}", profileId: profileId, cancellationToken: cancellationToken);

            if (changes.Count == 0)
            {
                await log.AppendAsync("No changes detected.");
                return;
            }

            await log.AppendAsync(changes.ToArray());
        }

        private static string YesNo(bool value) => value ? "Yes" : "No";

        // A short human-readable summary of a profile's notification-tab settings for the operation log.
        private static string NotificationsSummary(bool enabled, bool onStart, bool onComplete, bool onEject, bool showProgressWindow)
        {
            string balloon;
            if (!enabled)
            {
                balloon = "Off";
            }
            else
            {
                var events = new List<string>();
                if (onStart)
                {
                    events.Add("started");
                }
                if (onComplete)
                {
                    events.Add("completed");
                }
                if (onEject)
                {
                    events.Add("ejected");
                }
                balloon = events.Count == 0 ? "On (no events)" : $"On ({string.Join(", ", events)})";
            }
            return showProgressWindow ? $"{balloon} + progress window" : balloon;
        }
    }
}
