namespace BackupService.FileSystem
{
    /// <summary>
    /// A stream from <see cref="IBackupFileSystem.OpenWrite"/> whose content only lands at the destination when it's
    /// closed — Google Drive buffers locally and uploads on close. Without a way to say "don't", a copy that failed or
    /// was stopped part-way would still upload its partial file on the way out (a Stop then took as long as the
    /// upload), only for the crash-safe copy to delete it.
    /// </summary>
    public interface IAbandonableWrite
    {
        /// <summary>Makes closing the stream discard what was written instead of finishing it.</summary>
        void Abandon();
    }

    /// <summary>Helper for the copy code, which holds a plain <see cref="Stream"/>.</summary>
    public static class AbandonableWrite
    {
        /// <summary>Abandons <paramref name="stream"/> if it supports it — call when the copy into it has failed.</summary>
        public static void Abandon(Stream stream)
        {
            if (stream is IAbandonableWrite write)
            {
                write.Abandon();
            }
        }
    }
}
