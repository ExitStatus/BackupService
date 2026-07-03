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
    }
}
