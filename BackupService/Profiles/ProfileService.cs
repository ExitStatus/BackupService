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
            string sourceFolder,
            string targetFolder,
            bool watchFolder,
            string? scheduleCron,
            CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            db.Profiles.Add(new Profile
            {
                Name = name,
                Description = description,
                Type = type,
                Schedule = scheduleCron,
                DateCreated = DateTimeOffset.UtcNow,
                Status = ProfileStatus.Idle,
                FolderPairs =
                {
                    new FolderPair
                    {
                        SourceFolder = sourceFolder,
                        TargetFolder = targetFolder,
                        WatchFolder = watchFolder,
                        Status = FolderPairStatus.Idle,
                        LastRunStatus = FolderPairLastRunStatus.None,
                    },
                },
            });

            await db.SaveChangesAsync(cancellationToken);
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

        public async Task UpdateAsync(
            int id,
            string name,
            string? description,
            string sourceFolder,
            string targetFolder,
            bool watchFolder,
            string? scheduleCron,
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

            var pair = profile.FolderPairs.FirstOrDefault();
            if (pair is null)
            {
                pair = new FolderPair
                {
                    SourceFolder = sourceFolder,
                    TargetFolder = targetFolder,
                    Status = FolderPairStatus.Idle,
                    LastRunStatus = FolderPairLastRunStatus.None,
                };
                profile.FolderPairs.Add(pair);
            }

            pair.SourceFolder = sourceFolder;
            pair.TargetFolder = targetFolder;
            pair.WatchFolder = watchFolder;

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
