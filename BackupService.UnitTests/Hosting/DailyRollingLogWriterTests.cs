using BackupService.Hosting;
using FluentAssertions;

namespace BackupService.UnitTests.Hosting
{
    [TestFixture]
    public class DailyRollingLogWriterTests
    {
        private static readonly DateOnly Today = new(2026, 6, 27);

        [Test]
        public void SelectExpiredLogFiles_SelectsOnlyFilesOlderThanRetention()
        {
            // 14-day retention from 2026-06-27 → cutoff 2026-06-13. Files dated before the cutoff are expired.
            string[] files =
            [
                @"C:\logs\backupservice-2026-06-12.log", // expired (before cutoff)
                @"C:\logs\backupservice-2026-06-13.log", // kept (== cutoff, not < cutoff)
                @"C:\logs\backupservice-2026-06-27.log", // today, kept
            ];

            var expired = DailyRollingLogWriter.SelectExpiredLogFiles(files, Today, retentionDays: 14).ToList();

            expired.Should().ContainSingle()
                .Which.Should().Be(@"C:\logs\backupservice-2026-06-12.log");
        }

        [Test]
        public void SelectExpiredLogFiles_IgnoresFilesThatDoNotMatchThePattern()
        {
            string[] files =
            [
                @"C:\logs\backupservice-2000-01-01.log", // expired and matching → selected
                @"C:\logs\backupservice-notadate.log",   // wrong date part → ignored
                @"C:\logs\other-2000-01-01.log",         // wrong prefix → ignored
                @"C:\logs\backupservice-2000-01-01.txt",  // wrong extension → ignored
            ];

            var expired = DailyRollingLogWriter.SelectExpiredLogFiles(files, Today, retentionDays: 14).ToList();

            expired.Should().ContainSingle()
                .Which.Should().Be(@"C:\logs\backupservice-2000-01-01.log");
        }

        [Test]
        public void SelectExpiredLogFiles_NothingExpiredWhenAllRecent()
        {
            string[] files =
            [
                @"C:\logs\backupservice-2026-06-20.log",
                @"C:\logs\backupservice-2026-06-27.log",
            ];

            DailyRollingLogWriter.SelectExpiredLogFiles(files, Today, retentionDays: 14).Should().BeEmpty();
        }
    }
}
