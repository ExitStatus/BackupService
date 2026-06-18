namespace BackupService.Database
{
    /// <summary>
    /// Single-row settings for how long log history is kept before the retention purge removes it.
    /// (One row, like <see cref="AdminCredential"/>; seeded lazily with the defaults below.)
    /// </summary>
    public class LogRetentionSettings
    {
        public int Id { get; set; }

        /// <summary>Days of <see cref="AuthenticationHistory"/> to keep. Defaults to 7.</summary>
        public int AuthenticationLogRetentionDays { get; set; } = 7;

        /// <summary>Days of <see cref="OperationLog"/> history to keep. Defaults to 30.</summary>
        public int OperationLogRetentionDays { get; set; } = 30;
    }
}
