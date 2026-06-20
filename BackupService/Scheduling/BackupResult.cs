namespace BackupService.Scheduling
{
    /// <summary>Mutable running tally of the operations performed during a folder-pair sync.</summary>
    public sealed class BackupResult
    {
        public int Copied { get; set; }

        public int Updated { get; set; }

        public int Deleted { get; set; }

        public int Errors { get; set; }

        /// <summary>Non-fatal issues (e.g. a file skipped because it was locked by another process).</summary>
        public int Warnings { get; set; }

        /// <summary>Total bytes of data actually written to the target (copies/updates/archives).</summary>
        public long BytesCopied { get; set; }

        public void Add(BackupResult other)
        {
            Copied += other.Copied;
            Updated += other.Updated;
            Deleted += other.Deleted;
            Errors += other.Errors;
            Warnings += other.Warnings;
            BytesCopied += other.BytesCopied;
        }
    }
}
