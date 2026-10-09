using BackupService.DatabaseBackup;
using FluentAssertions;

namespace BackupService.UnitTests.DatabaseBackup
{
    [TestFixture]
    public class PendingRestoreApplierTests
    {
        private string _dataDir = null!;
        private string _dbPath = null!;

        [SetUp]
        public void SetUp()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), "BackupServiceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            _dbPath = Path.Combine(_dataDir, "backupservice.db");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        [Test]
        public void ApplyIfPending_NoPendingFile_DoesNothing()
        {
            File.WriteAllText(_dbPath, "current");

            var applied = PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error);

            applied.Should().BeFalse();
            error.Should().BeNull();
            File.ReadAllText(_dbPath).Should().Be("current");
        }

        [Test]
        public void ApplyIfPending_SwapsThePendingFileOverTheDatabase()
        {
            File.WriteAllText(_dbPath, "current");
            File.WriteAllText(_dbPath + "-wal", "wal");
            File.WriteAllText(_dbPath + "-shm", "shm");
            File.WriteAllText(PendingRestoreApplier.PendingPathFor(_dataDir), "restored");

            var applied = PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error);

            applied.Should().BeTrue();
            error.Should().BeNull();
            File.ReadAllText(_dbPath).Should().Be("restored");
            // The old database is kept as insurance; its journal files are gone (they belong to the
            // replaced file); the staged file is consumed.
            File.ReadAllText(_dbPath + ".pre-restore").Should().Be("current");
            // Not a real database, so the WAL couldn't be folded in — it's kept beside the insurance copy instead.
            File.ReadAllText(_dbPath + ".pre-restore-wal").Should().Be("wal");
            File.Exists(_dbPath + "-wal").Should().BeFalse();
            File.Exists(_dbPath + "-shm").Should().BeFalse();
            File.Exists(PendingRestoreApplier.PendingPathFor(_dataDir)).Should().BeFalse();
        }

        [Test]
        public void ApplyIfPending_NoExistingDatabase_StillApplies()
        {
            File.WriteAllText(PendingRestoreApplier.PendingPathFor(_dataDir), "restored");

            var applied = PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error);

            applied.Should().BeTrue();
            error.Should().BeNull();
            File.ReadAllText(_dbPath).Should().Be("restored");
            File.Exists(_dbPath + ".pre-restore").Should().BeFalse();
        }

        [Test]
        public void ApplyIfPending_Failure_LeavesTheStagedFileForRetry()
        {
            File.WriteAllText(_dbPath, "current");
            var pending = PendingRestoreApplier.PendingPathFor(_dataDir);
            File.WriteAllText(pending, "restored");

            // Hold the live database open with no sharing so the swap's copy fails.
            using (File.Open(_dbPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var applied = PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error);

                applied.Should().BeFalse();
                error.Should().NotBeNull();
            }

            File.Exists(pending).Should().BeTrue("a failed restore must stay staged so the next startup retries");
            File.ReadAllText(_dbPath).Should().Be("current");
        }

        [Test]
        public void ApplyIfPending_InsuranceCopy_IncludesDataThatWasOnlyInTheWal()
        {
            // The live database runs in WAL mode and is never checkpointed on exit, so its rows can sit entirely in
            // -wal. The .pre-restore copy has to contain them — on its own, since that's what someone would rename back.
            CreateDatabaseWithRowsOnlyInTheWal(_dbPath, rows: 50);
            CountRows(CopyOf(_dbPath, "main-file-only.db")).Should().BeNull("precondition: the main file alone has none of the data");
            CreateDatabase(PendingRestoreApplier.PendingPathFor(_dataDir));

            var applied = PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error);

            applied.Should().BeTrue(error);
            TryDelete(_dbPath + ".pre-restore-wal"); // prove the copy stands alone
            CountRows(_dbPath + ".pre-restore").Should().Be(50);
        }

        [Test]
        public void ApplyIfPending_SwapFailure_PutsTheLiveDatabasesWalBack()
        {
            if (!OperatingSystem.IsWindows())
            {
                // Forcing the swap to fail relies on an open handle blocking the replace — Windows locking. Unix
                // locks are advisory, so the replace would just succeed.
                Assert.Ignore("Needs Windows file locking to make the swap fail.");
                return;
            }
            File.WriteAllText(_dbPath, "current");
            File.WriteAllText(_dbPath + "-wal", "wal");
            var pending = PendingRestoreApplier.PendingPathFor(_dataDir);
            File.WriteAllText(pending, "restored");

            // Readable (so the insurance copy works) but not replaceable, so the final swap fails.
            using (File.Open(_dbPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error).Should().BeFalse();
                error.Should().NotBeNull();
            }

            File.ReadAllText(_dbPath).Should().Be("current");
            File.ReadAllText(_dbPath + "-wal").Should().Be("wal", "the live database must not be left without its journal");
            File.Exists(_dbPath + ".restoring").Should().BeFalse();
            File.Exists(pending).Should().BeTrue();
        }

        [Test]
        public void ApplyIfPending_MovesAsideLogFilesTheRestoredDatabaseDoesNotKnow()
        {
            // The restored database will hand out ids 4, 5, … again; their old files must not be appended to.
            var logs = Path.Combine(_dataDir, "operation-logs");
            Directory.CreateDirectory(logs);
            foreach (var id in new[] { 1, 2, 3, 4, 5 })
            {
                File.WriteAllText(Path.Combine(logs, $"{id}.log"), $"log {id}");
            }
            CreateDatabase(PendingRestoreApplier.PendingPathFor(_dataDir), operationLogIds: [1, 2, 3]);

            PendingRestoreApplier.ApplyIfPending(_dataDir, _dbPath, out var error).Should().BeTrue(error);

            Directory.GetFiles(logs, "*.log").Select(Path.GetFileName).Should().BeEquivalentTo("1.log", "2.log", "3.log");
            Directory.GetFiles(Path.Combine(logs, PendingRestoreApplier.SupersededLogsFolderName)).Select(Path.GetFileName)
                .Should().BeEquivalentTo("4.log", "5.log");
        }

        // ---- helpers ----

        private static string Connection(string path) =>
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

        private static void CreateDatabase(string path, int[]? operationLogIds = null)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Connection(path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE OperationLogs (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT);"
                + string.Concat((operationLogIds ?? []).Select(id => $"INSERT INTO OperationLogs (Id, Name) VALUES ({id}, 'x');"));
            command.ExecuteNonQuery();
        }

        // Builds a WAL-mode database whose rows are all still in -wal (as the live app leaves it), by copying the
        // files while the writing connection is open and auto-checkpointing is off.
        private static void CreateDatabaseWithRowsOnlyInTheWal(string path, int rows)
        {
            var source = path + ".source";
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(Connection(source)))
            {
                connection.Open();
                using (var setup = connection.CreateCommand())
                {
                    setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE T (x INTEGER);"
                        + string.Concat(Enumerable.Range(1, rows).Select(i => $"INSERT INTO T (x) VALUES ({i});"));
                    setup.ExecuteNonQuery();
                }

                CopyShared(source, path);
                CopyShared(source + "-wal", path + "-wal");
            }
            TryDelete(source);
            TryDelete(source + "-wal");
            TryDelete(source + "-shm");
        }

        private static void CopyShared(string from, string to)
        {
            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(to);
            input.CopyTo(output);
        }

        private string CopyOf(string path, string name)
        {
            var copy = Path.Combine(_dataDir, name);
            File.Copy(path, copy);
            return copy;
        }

        // Rows in table T, or null when the table isn't there.
        private static long? CountRows(string path)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Connection(path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM T;";
            try
            {
                return (long)command.ExecuteScalar()!;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
