namespace BackupService.Scheduling
{
    /// <summary>Mutable running tally of the operations performed during a folder-pair sync.</summary>
    public sealed class BackupResult
    {
        public int Copied { get; set; }

        public int Updated { get; set; }

        public int Deleted { get; set; }

        public int Errors { get; set; }

        public void Add(BackupResult other)
        {
            Copied += other.Copied;
            Updated += other.Updated;
            Deleted += other.Deleted;
            Errors += other.Errors;
        }
    }
}
