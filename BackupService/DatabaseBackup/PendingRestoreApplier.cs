namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Applies a staged database restore (written by <see cref="IDatabaseBackupService.StageRestoreAsync"/>)
    /// by swapping the pending file over the live database. Runs from <c>Program.cs</c> <b>before anything
    /// opens the database</b>, so the swap can't race a live SQLite connection — restoring in-process
    /// while the app runs would risk corrupting the file under an open writer. Pure file operations,
    /// path-parameterised for tests.
    /// </summary>
    public static class PendingRestoreApplier
    {
        public const string PendingFileName = "pending-restore.db";

        /// <summary>The staged-restore path inside <paramref name="dataDirectory"/>.</summary>
        public static string PendingPathFor(string dataDirectory) => Path.Combine(dataDirectory, PendingFileName);

        /// <summary>
        /// If a staged restore exists, swaps it over <paramref name="databasePath"/>: the current
        /// database is kept beside it as <c>.pre-restore</c> (insurance), stale <c>-wal</c>/<c>-shm</c>
        /// files are removed (they belong to the old database), and the staged file is consumed.
        /// Returns whether a restore was applied; on failure the staged file is left in place so the
        /// next startup retries, and <paramref name="error"/> carries the reason.
        /// </summary>
        public static bool ApplyIfPending(string dataDirectory, string databasePath, out string? error)
        {
            error = null;

            var pending = PendingPathFor(dataDirectory);
            if (!File.Exists(pending))
            {
                return false;
            }

            try
            {
                if (File.Exists(databasePath))
                {
                    File.Copy(databasePath, databasePath + ".pre-restore", overwrite: true);
                }

                // The WAL/SHM belong to the database being replaced — a restored file must not start
                // with another database's journal.
                TryDelete(databasePath + "-wal");
                TryDelete(databasePath + "-shm");

                File.Copy(pending, databasePath, overwrite: true);
                File.Delete(pending);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort — a missing/locked journal shouldn't abort the restore attempt.
            }
        }
    }
}
