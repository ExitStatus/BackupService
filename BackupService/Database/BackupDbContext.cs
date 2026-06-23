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

        public DbSet<ArchiveSyncItem> ArchiveSyncItems => Set<ArchiveSyncItem>();

        public DbSet<FolderPairFilter> FolderPairFilters => Set<FolderPairFilter>();

        public DbSet<ArchiveSyncFilter> ArchiveSyncFilters => Set<ArchiveSyncFilter>();

        public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

        public DbSet<OperationLogDetail> OperationLogDetails => Set<OperationLogDetail>();

        public DbSet<LogRetentionSettings> LogRetentionSettings => Set<LogRetentionSettings>();

        public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

        public DbSet<Connection> Connections => Set<Connection>();

        public DbSet<SmbConnectionSettings> SmbConnectionSettings => Set<SmbConnectionSettings>();

        public DbSet<GoogleDriveConnectionSettings> GoogleDriveConnectionSettings => Set<GoogleDriveConnectionSettings>();

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

            // SMB settings are a 1:1 child of a connection, keyed by the connection id and
            // cascade-deleted with it.
            modelBuilder.Entity<SmbConnectionSettings>()
                .HasKey(s => s.ConnectionId);

            modelBuilder.Entity<Connection>()
                .HasOne(c => c.Smb)
                .WithOne(s => s.Connection)
                .HasForeignKey<SmbConnectionSettings>(s => s.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Google Drive settings are likewise a 1:1 cascade-deleted child of a connection.
            modelBuilder.Entity<GoogleDriveConnectionSettings>()
                .HasKey(s => s.ConnectionId);

            modelBuilder.Entity<Connection>()
                .HasOne(c => c.GoogleDrive)
                .WithOne(s => s.Connection)
                .HasForeignKey<GoogleDriveConnectionSettings>(s => s.ConnectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // A folder pair may point its source and/or target at a connection. Restrict the delete
            // (don't cascade or null it) — the connection service blocks deleting a connection in use.
            modelBuilder.Entity<FolderPair>()
                .HasOne(fp => fp.SourceConnection)
                .WithMany()
                .HasForeignKey(fp => fp.SourceConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<FolderPair>()
                .HasOne(fp => fp.TargetConnection)
                .WithMany()
                .HasForeignKey(fp => fp.TargetConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            // InstantSync and ArchiveSync items can likewise point a side at a connection (same Restrict).
            modelBuilder.Entity<InstantSyncItem>()
                .HasOne(i => i.SourceConnection)
                .WithMany()
                .HasForeignKey(i => i.SourceConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<InstantSyncItem>()
                .HasOne(i => i.TargetConnection)
                .WithMany()
                .HasForeignKey(i => i.TargetConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ArchiveSyncItem>()
                .HasOne(a => a.SourceConnection)
                .WithMany()
                .HasForeignKey(a => a.SourceConnectionId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ArchiveSyncItem>()
                .HasOne(a => a.TargetConnection)
                .WithMany()
                .HasForeignKey(a => a.TargetConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
