using Microsoft.EntityFrameworkCore;

namespace BackupService.Database
{
    /// <summary>
    /// EF Core database context for the backup service. Backed by SQLite.
    /// </summary>
    public class BackupDbContext(DbContextOptions<BackupDbContext> options) : DbContext(options)
    {
        public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

        public DbSet<AdminCredential> AdminCredentials => Set<AdminCredential>();

        public DbSet<AuthenticationHistory> AuthenticationHistory => Set<AuthenticationHistory>();

        public DbSet<Profile> Profiles => Set<Profile>();

        public DbSet<FolderPair> FolderPairs => Set<FolderPair>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // New profiles default to enabled; existing rows backfill to true on migration.
            modelBuilder.Entity<Profile>()
                .Property(p => p.Enabled)
                .HasDefaultValue(true);
        }
    }
}
