using System.ComponentModel.DataAnnotations;

namespace BackupService.Database
{
    /// <summary>
    /// The single administrator credential for the service. Only one row is ever
    /// expected. The password is stored hashed (never in plaintext). Username is
    /// currently fixed to "admin" but stored here so it can be made editable later.
    /// </summary>
    public class AdminCredential
    {
        public int Id { get; set; }

        [MaxLength(256)]
        public required string Username { get; set; }

        [MaxLength(512)]
        public required string PasswordHash { get; set; }
    }
}
