using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Profiles
{
    /// <summary>
    /// Default <see cref="IProfileService"/>. Persists via the DbContext factory (a
    /// short-lived context per call, per the project's factory convention).
    /// </summary>
    public sealed class ProfileService(IDatabaseContextFactory contextFactory) : IProfileService
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

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
            if (profile is null)
            {
                return;
            }

            db.Profiles.Remove(profile);
            await db.SaveChangesAsync(cancellationToken);
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
        }
    }
}
