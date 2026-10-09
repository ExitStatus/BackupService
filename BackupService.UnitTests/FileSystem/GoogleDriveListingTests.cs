using BackupService.FileSystem.GoogleDrive;
using FluentAssertions;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace BackupService.UnitTests.FileSystem
{
    /// <summary>
    /// Covers how the Drive filesystem maps Drive's id-based items onto the paths the sync engines work in: names that
    /// can't be told apart, names that would escape the target folder, and which write time to trust.
    /// </summary>
    [TestFixture]
    public class GoogleDriveListingTests
    {
        private static DriveFile Named(string name) => new() { Name = name, Id = Guid.NewGuid().ToString("N") };

        [Test]
        public void SameNamedSiblings_FailTheListing_RatherThanBackingUpOneOfThemTwice()
        {
            var act = () => GoogleDriveBackupFileSystem.MappableChildren("Photos", [Named("2023"), Named("2024"), Named("2023")]);

            act.Should().Throw<IOException>().WithMessage("*'Photos'*more than one item named '2023'*");
        }

        [Test]
        public void NamesDifferingOnlyInCase_CountAsTheSame()
        {
            // The engines compare names ignoring case, so these would collide on the target.
            var act = () => GoogleDriveBackupFileSystem.MappableChildren("Docs", [Named("Report.pdf"), Named("report.pdf")]);

            act.Should().Throw<IOException>();
        }

        [Test]
        public void DotAndDotDotNames_AreLeftOut()
        {
            // Joined onto a local target path, ".." would escape the target folder.
            var mappable = GoogleDriveBackupFileSystem.MappableChildren("Shared", [Named(".."), Named("."), Named("ok.txt")]);

            mappable.Select(f => f.Name).Should().BeEquivalentTo("ok.txt");
        }

        private static DriveFile Timed(long? stampedTicks, string? modifiedTime) => new()
        {
            Name = "a.xlsx",
            ModifiedTimeRaw = modifiedTime,
            AppProperties = stampedTicks is { } t
                ? new Dictionary<string, string> { ["backupServiceWriteTicks"] = t.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                : null,
        };

        private static readonly DateTime Stamped = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

        [Test]
        public void StampedTicks_AreUsed_WhileDrivesModifiedTimeStillAgrees()
        {
            // Drive keeps only milliseconds; the stamp restores the exact time this app wrote.
            var file = Timed(Stamped.Ticks, "2026-01-01T10:00:00.123Z");

            GoogleDriveBackupFileSystem.ParseWriteTime(file).Should().Be(Stamped);
        }

        [Test]
        public void StampedTicks_AreIgnored_OnceTheFileWasEditedInDrive()
        {
            // An edit in Drive moves modifiedTime on but keeps the old stamp; trusting the stamp would hide the edit.
            var file = Timed(Stamped.Ticks, "2026-03-05T08:30:00.000Z");

            GoogleDriveBackupFileSystem.ParseWriteTime(file).Should().Be(new DateTime(2026, 3, 5, 8, 30, 0, DateTimeKind.Utc));
        }

        [Test]
        public void WithoutAStamp_DrivesModifiedTimeIsUsed()
        {
            GoogleDriveBackupFileSystem.ParseWriteTime(Timed(null, "2026-03-05T08:30:00.000Z"))
                .Should().Be(new DateTime(2026, 3, 5, 8, 30, 0, DateTimeKind.Utc));
        }
    }
}
