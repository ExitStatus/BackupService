using BackupService.DatabaseBackup;
using FluentAssertions;

namespace BackupService.UnitTests.DatabaseBackup
{
    [TestFixture]
    public class DatabaseBackupNamingTests
    {
        [Test]
        public void BuildFileName_RoundTripsThroughTryParse()
        {
            var timestamp = new DateTime(2026, 7, 3, 14, 30, 55);

            var fileName = DatabaseBackupNaming.BuildFileName(timestamp);

            fileName.Should().Be("database-backup_2026-07-03_143055.zip");
            DatabaseBackupNaming.TryParseTimestamp(fileName, out var parsed).Should().BeTrue();
            parsed.Should().Be(timestamp);
        }

        [Test]
        public void TryParse_AcceptsAFullPath()
        {
            DatabaseBackupNaming.TryParseTimestamp(@"C:\Backups\database-backup_2026-01-02_030405.zip", out var parsed).Should().BeTrue();
            parsed.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5));
        }

        [TestCase("notes.txt")]
        [TestCase("database-backup_.zip")]
        [TestCase("database-backup_2026-99-99_000000.zip")]
        [TestCase("other_2026-01-01_000000.zip")]
        [TestCase("database-backup_2026-01-01_000000.txt")]
        [TestCase("")]
        [TestCase(null)]
        public void TryParse_RejectsForeignAndMalformedNames(string? fileName)
        {
            DatabaseBackupNaming.TryParseTimestamp(fileName, out _).Should().BeFalse();
        }

        [Test]
        public void SelectExpired_KeepsTheNewest_ReturnsTheOldestFirst()
        {
            var files = new[]
            {
                @"T:\db\database-backup_2026-01-03_000000.zip",
                @"T:\db\database-backup_2026-01-01_000000.zip",
                @"T:\db\database-backup_2026-01-05_000000.zip",
                @"T:\db\database-backup_2026-01-02_000000.zip",
                @"T:\db\database-backup_2026-01-04_000000.zip",
            };

            var expired = DatabaseBackupNaming.SelectExpired(files, keep: 2);

            expired.Should().Equal(
                "database-backup_2026-01-01_000000.zip",
                "database-backup_2026-01-02_000000.zip",
                "database-backup_2026-01-03_000000.zip");
        }

        [Test]
        public void SelectExpired_IgnoresForeignFiles()
        {
            var files = new[]
            {
                @"T:\db\database-backup_2026-01-01_000000.zip",
                @"T:\db\database-backup_2026-01-02_000000.zip",
                @"T:\db\holiday-photos.zip",
                @"T:\db\readme.txt",
            };

            var expired = DatabaseBackupNaming.SelectExpired(files, keep: 1);

            expired.Should().Equal("database-backup_2026-01-01_000000.zip");
        }

        [Test]
        public void SelectExpired_ClampsKeepToAtLeastOne()
        {
            var files = new[]
            {
                "database-backup_2026-01-01_000000.zip",
                "database-backup_2026-01-02_000000.zip",
            };

            // keep 0 would delete everything just created — clamp to 1.
            var expired = DatabaseBackupNaming.SelectExpired(files, keep: 0);

            expired.Should().Equal("database-backup_2026-01-01_000000.zip");
        }

        [Test]
        public void SelectExpired_NothingBeyondKeep_ReturnsEmpty()
        {
            var files = new[] { "database-backup_2026-01-01_000000.zip" };

            DatabaseBackupNaming.SelectExpired(files, keep: 5).Should().BeEmpty();
        }
    }
}
