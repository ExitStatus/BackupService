using System.Globalization;
using BackupService.Logging;
using Microsoft.Data.Sqlite;

namespace BackupService.DatabaseBackup
{
    /// <summary>
    /// Applies a staged database restore (written by <see cref="IDatabaseBackupService.StageRestoreAsync"/>)
    /// by swapping the pending file over the live database. Runs from <c>Program.cs</c> <b>before anything
    /// opens the database</b>, so the swap can't race a live SQLite connection — restoring in-process
    /// while the app runs would risk corrupting the file under an open writer. Path-parameterised for tests.
    /// <para>
    /// The database runs in WAL mode and its connections are pooled until the process exits, so committed data
    /// can sit entirely in <c>-wal</c> with the main file holding little more than a header. So the insurance copy
    /// takes the WAL with it (folded into one file when that can be done safely, on a scratch copy), the live
    /// database is never opened, and its WAL is only ever set aside — never deleted — until the restored file is
    /// safely in place.
    /// </para>
    /// </summary>
    public static class PendingRestoreApplier
    {
        public const string PendingFileName = "pending-restore.db";

        /// <summary>The sub-folder of the operation-log directory that log files the restored database doesn't know about are moved to.</summary>
        public const string SupersededLogsFolderName = "superseded-by-restore";

        /// <summary>The staged-restore path inside <paramref name="dataDirectory"/>.</summary>
        public static string PendingPathFor(string dataDirectory) => Path.Combine(dataDirectory, PendingFileName);

        /// <summary>
        /// If a staged restore exists, swaps it over <paramref name="databasePath"/>: the current database (with its
        /// WAL folded in) is kept beside it as <c>.pre-restore</c> (insurance), its <c>-wal</c>/<c>-shm</c> are
        /// removed (they belong to the old database), and the staged file is consumed. Operation-log files newer than
        /// anything the restored database knows are moved aside, so the ids it hands out next can't append to them.
        /// Returns whether a restore was applied; on failure the live database is left as it was, the staged file
        /// stays in place so the next startup retries, and <paramref name="error"/> carries the reason.
        /// </summary>
        public static bool ApplyIfPending(string dataDirectory, string databasePath, out string? error)
        {
            error = null;

            var pending = PendingPathFor(dataDirectory);
            if (!File.Exists(pending))
            {
                return false;
            }

            // Claimed before anything changes, by renaming it, so it's applied at most once: deleting it only after the
            // swap could fail (a scanner holding it), and the next startup would then apply the same restore again —
            // over everything changed since, and over the insurance copy. A leftover ".applying" is never picked up.
            var applying = pending + ".applying";
            try
            {
                File.Move(pending, applying, overwrite: true);
            }
            catch (Exception ex)
            {
                error = ex.Message; // nothing has changed; the next startup tries again
                return false;
            }

            var wal = databasePath + "-wal";
            var shm = databasePath + "-shm";
            var incoming = databasePath + ".restoring";
            try
            {
                if (File.Exists(databasePath))
                {
                    // Insurance first, and complete: the main file AND its WAL, as a pair SQLite opens together
                    // ("{file}-wal" beside "{file}"). Then, best-effort, fold that copy into one standalone file.
                    // The live database itself is never opened here — only copied.
                    var preRestore = databasePath + ".pre-restore";
                    File.Copy(databasePath, preRestore, overwrite: true);
                    if (File.Exists(wal))
                    {
                        File.Copy(wal, preRestore + "-wal", overwrite: true);
                        TryConsolidate(preRestore);
                    }
                    else
                    {
                        TryDelete(preRestore + "-wal"); // a stale one from an earlier restore would corrupt this copy
                    }
                }

                // Stage the restored file beside the database. A failure up to here has changed nothing.
                File.Copy(applying, incoming, overwrite: true);

                // Swap. The old WAL/SHM must not be applied to the restored file, so they're set aside first — and
                // put back if the swap fails, so the live database is never left without its journal.
                var walAside = SetAside(wal);
                var shmAside = SetAside(shm);
                try
                {
                    File.Move(incoming, databasePath, overwrite: true);
                }
                catch
                {
                    PutBack(walAside, wal);
                    PutBack(shmAside, shm);
                    throw;
                }
                TryDelete(walAside);
                TryDelete(shmAside);
            }
            catch (Exception ex)
            {
                TryDelete(incoming);
                PutBack(applying, pending); // not applied — stage it again so the next startup retries
                error = ex.Message;
                return false;
            }

            TryDelete(applying); // consumed; one that can't be deleted is harmless (only pending-restore.db is applied)
            ArchiveSupersededLogFiles(dataDirectory, databasePath);
            return true;
        }

        // Turns the insurance pair ("{copy}" + "{copy}-wal") into one standalone file, so restoring it by hand is a
        // single rename. Works on a scratch copy of the pair: SQLite may delete a WAL it can't use, and the pair is
        // the only complete copy of the old data. Only a checkpoint that succeeds replaces the pair; anything else
        // leaves the pair as it is.
        private static void TryConsolidate(string copyPath)
        {
            var scratch = copyPath + ".consolidating";
            try
            {
                File.Copy(copyPath, scratch, overwrite: true);
                File.Copy(copyPath + "-wal", scratch + "-wal", overwrite: true);
                using (var connection = new SqliteConnection(ConnectionString(scratch, SqliteOpenMode.ReadWrite)))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    using var reader = command.ExecuteReader();
                    // Columns: busy, WAL frames, frames checkpointed. Busy or a partial checkpoint isn't consolidated.
                    if (!reader.Read() || reader.GetInt64(0) != 0 || reader.GetInt64(1) != reader.GetInt64(2))
                    {
                        return;
                    }
                }

                File.Move(scratch, copyPath, overwrite: true);
                TryDelete(copyPath + "-wal");
            }
            catch
            {
                // Keep the pair.
            }
            finally
            {
                TryDelete(scratch);
                TryDelete(scratch + "-wal");
                TryDelete(scratch + "-shm");
            }
        }

        // A restore rewinds the database's AUTOINCREMENT ids, but log files are named by id and live outside the
        // database. Any file newer than the restored database's highest log id would otherwise be appended to by the
        // next log that reuses its id, mixing another run's lines into it — so those are moved to a sub-folder
        // (kept, not deleted: they're the history of runs made after the backup was taken). Best-effort.
        private static void ArchiveSupersededLogFiles(string dataDirectory, string databasePath)
        {
            var logDirectory = Path.Combine(dataDirectory, OperationLogFileStore.DirectoryName);
            if (!Directory.Exists(logDirectory))
            {
                return;
            }

            long highestKnownId;
            try
            {
                // ReadWrite: a WAL-mode file can't always be opened read-only without its -shm. Nothing else has the
                // database open yet, and nothing is written.
                using var connection = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadWrite));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(MAX(Id), 0) FROM OperationLogs;";
                highestKnownId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch
            {
                return; // can't tell which files are superseded — leave them
            }

            var archive = Path.Combine(logDirectory, SupersededLogsFolderName);
            foreach (var file in Directory.GetFiles(logDirectory, "*.log"))
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id <= highestKnownId)
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(archive);
                    File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
                }
                catch
                {
                    // Best-effort per file.
                }
            }
        }

        // Pooling off so the file handle is released as soon as the connection is disposed (the swap needs it closed).
        private static string ConnectionString(string databasePath, SqliteOpenMode mode) =>
            new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Pooling = false }.ToString();

        // Renames a journal file out of the way, returning where it went (or null when there wasn't one).
        private static string? SetAside(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var aside = path + ".pre-restore-swap";
            File.Move(path, aside, overwrite: true);
            return aside;
        }

        private static void PutBack(string? aside, string original)
        {
            if (aside is null)
            {
                return;
            }
            try
            {
                File.Move(aside, original, overwrite: true);
            }
            catch
            {
                // Best-effort — the error that's propagating is the one to report.
            }
        }

        private static void TryDelete(string? path)
        {
            if (path is null)
            {
                return;
            }
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort — a missing/locked file shouldn't abort the restore attempt.
            }
        }
    }
}
