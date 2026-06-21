using BackupService.Database;
using BackupService.Security;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Connections
{
    /// <summary>
    /// One-time migration of stored SMB passwords from the old Windows-DPAPI format to the current
    /// cross-platform Data Protection format. Runs at startup and is idempotent: a password already in the
    /// current format is left alone, a legacy one is decrypted (via <see cref="ILegacySecretReader"/>) and
    /// re-encrypted with <see cref="ISecretProtector"/>. A value that can't be read either way is left as-is
    /// and logged — it must be re-entered.
    /// </summary>
    public sealed class ConnectionSecretMigrator(
        IDatabaseContextFactory contextFactory,
        ISecretProtector protector,
        ILegacySecretReader legacyReader,
        ILogger<ConnectionSecretMigrator> logger)
    {
        public void Migrate()
        {
            using var db = contextFactory.CreateDbContext();
            var settings = db.SmbConnectionSettings.ToList();

            var migrated = 0;
            var failed = 0;
            foreach (var setting in settings)
            {
                if (string.IsNullOrEmpty(setting.PasswordEncrypted) || IsCurrentFormat(setting.PasswordEncrypted))
                {
                    continue;
                }

                if (legacyReader.TryRead(setting.PasswordEncrypted, out var plaintext))
                {
                    setting.PasswordEncrypted = protector.Protect(plaintext);
                    migrated++;
                }
                else
                {
                    failed++;
                    logger.LogWarning(
                        "Connection {ConnectionId} has a stored password that could not be migrated to the new format; it must be re-entered.",
                        setting.ConnectionId);
                }
            }

            if (migrated > 0)
            {
                db.SaveChanges();
            }
            if (migrated > 0 || failed > 0)
            {
                logger.LogInformation("Connection password migration: {Migrated} migrated, {Failed} need re-entry.", migrated, failed);
            }
        }

        // A password is already in the current format if the current protector can decrypt it.
        private bool IsCurrentFormat(string value)
        {
            try
            {
                protector.Unprotect(value);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
