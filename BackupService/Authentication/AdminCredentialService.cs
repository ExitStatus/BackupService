using BackupService.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Authentication
{
    /// <summary>
    /// Default <see cref="IAdminCredentialService"/>. Creates a short-lived
    /// <see cref="BackupDbContext"/> per call via the factory (per the project's
    /// DbContext-factory convention) and hashes/verifies passwords with the
    /// framework's <see cref="PasswordHasher{TUser}"/> (PBKDF2, salted).
    /// </summary>
    public sealed class AdminCredentialService(
        IDatabaseContextFactory contextFactory,
        ILogger<AdminCredentialService> logger) : IAdminCredentialService
    {
        public const string DefaultUsername = "admin";
        private const string DefaultPassword = "admin";

        private readonly PasswordHasher<AdminCredential> _passwordHasher = new();

        public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            if (await db.AdminCredentials.AnyAsync(cancellationToken))
            {
                return;
            }

            var credential = new AdminCredential
            {
                Username = DefaultUsername,
                PasswordHash = string.Empty,
            };
            credential.PasswordHash = _passwordHasher.HashPassword(credential, DefaultPassword);

            db.AdminCredentials.Add(credential);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Seeded default admin credential for user {Username}.", DefaultUsername);
        }

        public async Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();

            var credential = await db.AdminCredentials.AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            if (credential is null
                || !string.Equals(credential.Username, username, StringComparison.Ordinal))
            {
                return false;
            }

            var result = _passwordHasher.VerifyHashedPassword(credential, credential.PasswordHash, password);
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
    }
}
