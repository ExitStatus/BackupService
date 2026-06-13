using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BackupService.Database
{
    /// <summary>
    /// Creates new <see cref="BackupDbContext"/> instances on demand. The context
    /// itself is intentionally not registered in DI; callers resolve this factory
    /// and own a short-lived context per unit of work.
    /// </summary>
    public interface IDatabaseContextFactory
    {
        BackupDbContext CreateDbContext();
    }

    /// <summary>
    /// Default <see cref="IDatabaseContextFactory"/>. Builds the (immutable) SQLite
    /// options once and hands out a fresh context each call, so it is safe to
    /// register as a singleton and share across threads.
    ///
    /// Also implements <see cref="IDesignTimeDbContextFactory{TContext}"/> so the
    /// EF Core tooling (<c>dotnet ef</c>) can create a context at design time even
    /// though the context is not registered in DI.
    /// </summary>
    public sealed class DatabaseContextFactory
        : IDatabaseContextFactory, IDesignTimeDbContextFactory<BackupDbContext>
    {
        private readonly DbContextOptions<BackupDbContext> _options = BuildOptions();

        public BackupDbContext CreateDbContext() => new(_options);

        // Used by the EF Core design-time tooling.
        BackupDbContext IDesignTimeDbContextFactory<BackupDbContext>.CreateDbContext(string[] args)
            => new(BuildOptions());

        private static DbContextOptions<BackupDbContext> BuildOptions()
            => new DbContextOptionsBuilder<BackupDbContext>()
                .UseSqlite(BackupDatabaseLocation.GetConnectionString())
                .Options;
    }
}
