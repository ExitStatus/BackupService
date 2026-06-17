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

        public DbSet<InstantSyncItem> InstantSyncItems => Set<InstantSyncItem>();

        public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

        public DbSet<OperationLogDetail> OperationLogDetails => Set<OperationLogDetail>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // New profiles default to enabled; existing rows backfill to true on migration.
            modelBuilder.Entity<Profile>()
                .Property(p => p.Enabled)
                .HasDefaultValue(true);

            // A log optionally belongs to a profile; deleting the profile cascade-deletes its
            // logs (cascade is the non-default behaviour for a nullable FK).
            modelBuilder.Entity<OperationLog>()
                .HasOne(l => l.Profile)
                .WithMany()
                .HasForeignKey(l => l.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
