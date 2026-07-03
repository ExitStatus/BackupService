using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Groups
{
    /// <summary>
    /// Default <see cref="IGroupService"/>. Persists via the DbContext factory (a short-lived context
    /// per call) and writes a profile-less operation log per mutation (mirrors <c>ConnectionService</c>).
    /// </summary>
    public sealed class GroupService(
        IDatabaseContextFactory contextFactory,
        IOperationLogFactory operationLogFactory) : IGroupService
    {
        public async Task<int> CreateAsync(string name, GroupConcurrency concurrency, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var group = new Group
            {
                Name = name,
                Concurrency = concurrency,
                DateCreated = DateTimeOffset.UtcNow,
            };

            db.Groups.Add(group);
            await db.SaveChangesAsync(cancellationToken);

            var log = await operationLogFactory.CreateAsync($"Group created: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync($"Name: {name}", $"Concurrency: {concurrency.GetDescription()}");

            return group.Id;
        }

        public async Task UpdateAsync(int id, string name, GroupConcurrency concurrency, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return;
            }

            var oldName = group.Name;
            var oldConcurrency = group.Concurrency;

            group.Name = name;
            group.Concurrency = concurrency;
            await db.SaveChangesAsync(cancellationToken);

            var changes = new List<string>();
            if (oldName != name)
            {
                changes.Add($"Name changed from '{oldName}' to '{name}'");
            }
            if (oldConcurrency != concurrency)
            {
                changes.Add($"Concurrency changed from '{oldConcurrency.GetDescription()}' to '{concurrency.GetDescription()}'");
            }

            var log = await operationLogFactory.CreateAsync($"Group updated: {oldName}", cancellationToken: cancellationToken);
            await log.AppendAsync(changes.Count == 0 ? ["No changes detected."] : changes.ToArray());
        }

        public async Task<PagedResult<Group>> GetPageAsync(int pageNumber, int pageSize, GroupSortColumn sortColumn, bool descending, CancellationToken cancellationToken = default)
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

            var query = db.Groups.AsNoTracking();
            var totalCount = await query.CountAsync(cancellationToken);
            var skip = (pageNumber - 1) * pageSize;

            // Name/Concurrency both sort fine in SQL (no DateTimeOffset column is sortable).
            IQueryable<Group> ordered = sortColumn switch
            {
                GroupSortColumn.Concurrency => descending
                    ? query.OrderByDescending(g => g.Concurrency).ThenBy(g => g.Name)
                    : query.OrderBy(g => g.Concurrency).ThenBy(g => g.Name),
                _ => descending
                    ? query.OrderByDescending(g => g.Name)
                    : query.OrderBy(g => g.Name),
            };
            var items = await ordered.Skip(skip).Take(pageSize).ToListAsync(cancellationToken);

            return new PagedResult<Group>(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Group?> GetAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        }

        public async Task<IReadOnlyList<GroupSummary>> GetSummariesAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Groups
                .AsNoTracking()
                .OrderBy(g => g.Name)
                .Select(g => new GroupSummary(g.Id, g.Name, g.Concurrency))
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyDictionary<int, GroupProfileStats>> GetProfileStatsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            // Pull grouped profiles into memory then aggregate — SQLite can't Max/compare a DateTimeOffset.
            var members = await db.Profiles
                .AsNoTracking()
                .Where(p => p.GroupId != null)
                .Select(p => new { GroupId = p.GroupId!.Value, p.Id, p.DateLastRun })
                .ToListAsync(cancellationToken);

            return members
                .GroupBy(m => m.GroupId)
                .ToDictionary(
                    g => g.Key,
                    g => new GroupProfileStats(
                        g.Count(),
                        g.Max(m => m.DateLastRun),
                        g.Select(m => m.Id).ToList()));
        }

        public async Task<int> DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return 0;
            }

            var name = group.Name;

            // Ungroup the members explicitly (belt-and-braces alongside the FK SetNull) and capture the count.
            var ungrouped = await db.Profiles
                .Where(p => p.GroupId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.GroupId, (int?)null), cancellationToken);

            db.Groups.Remove(group);
            await db.SaveChangesAsync(cancellationToken);

            var log = await operationLogFactory.CreateAsync($"Group deleted: {name}", cancellationToken: cancellationToken);
            await log.AppendAsync(
                $"Name: {name}",
                $"Profiles ungrouped: {ungrouped}");

            return ungrouped;
        }
    }
}
