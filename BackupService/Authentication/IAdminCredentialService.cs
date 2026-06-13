namespace BackupService.Authentication
{
    /// <summary>
    /// Manages the single administrator credential: seeding the default account,
    /// and verifying a username/password at login. Shaped to allow a future
    /// change-password operation without reworking callers.
    /// </summary>
    public interface IAdminCredentialService
    {
        /// <summary>
        /// Ensures an admin credential exists, creating the default
        /// (admin / admin, hashed) one if the table is empty.
        /// </summary>
        Task EnsureSeededAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true if the supplied username and password match the stored
        /// admin credential.
        /// </summary>
        Task<bool> VerifyAsync(string username, string password, CancellationToken cancellationToken = default);
    }
}
