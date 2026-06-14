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
    }
}
