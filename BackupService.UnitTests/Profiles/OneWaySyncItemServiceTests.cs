using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using FluentAssertions;

namespace BackupService.UnitTests.Profiles
{
    [TestFixture]
    public class OneWaySyncItemServiceTests
    {
        private readonly OneWaySyncItemService _service = new();

        private static OneWaySyncInput Input(int id, string name, string source = @"C:\S", string target = @"D:\T",
            bool allowDeletions = false, bool includeSubFolders = false, OverwriteBehaviour overwrite = OverwriteBehaviour.DoNotOverwriteNewer)
            => new(id, name, source, target, allowDeletions, includeSubFolders, overwrite);

        [Test]
        public void Add_AppendsNewPairsWithDefaultStatuses()
        {
            var profile = new Profile { Name = "P", DateCreated = DateTimeOffset.UtcNow };

            _service.Add(profile, [Input(0, "A"), Input(0, "B")]);

            profile.OneWaySyncItems.Select(p => p.Name).Should().Equal("A", "B");
            profile.OneWaySyncItems.Should().OnlyContain(p =>
                p.Status == OneWaySyncStatus.Idle && p.LastRunStatus == OneWaySyncLastRunStatus.None);
        }

        [Test]
        public void Sync_UpdatesMatchedAddsNewAndRemovesMissing()
        {
            var profile = new Profile { Name = "P", DateCreated = DateTimeOffset.UtcNow };
            profile.OneWaySyncItems.Add(new OneWaySyncItem { Id = 1, Name = "Keep", SourceFolder = @"C:\K", TargetFolder = @"D:\K" });
            profile.OneWaySyncItems.Add(new OneWaySyncItem { Id = 2, Name = "Drop", SourceFolder = @"C:\D", TargetFolder = @"D:\D" });

            _service.Sync(profile,
            [
                Input(1, "Keep", @"C:\K", @"E:\K2"), // matched, target changed
                Input(0, "New", @"C:\N", @"D:\N"),   // added
            ]);

            profile.OneWaySyncItems.Select(p => p.Name).Should().BeEquivalentTo("Keep", "New");
            profile.OneWaySyncItems.Single(p => p.Id == 1).TargetFolder.Should().Be(@"E:\K2");
            profile.OneWaySyncItems.Should().NotContain(p => p.Name == "Drop");
        }

        [Test]
        public void Sync_ReturnsHumanReadableChangeDescriptions()
        {
            var profile = new Profile { Name = "P", DateCreated = DateTimeOffset.UtcNow };
            profile.OneWaySyncItems.Add(new OneWaySyncItem { Id = 1, Name = "Old", SourceFolder = @"C:\S", TargetFolder = @"D:\T" });
            profile.OneWaySyncItems.Add(new OneWaySyncItem { Id = 2, Name = "Gone", SourceFolder = @"C:\G", TargetFolder = @"D:\G" });

            var changes = _service.Sync(profile,
            [
                Input(1, "Renamed", @"C:\S2", @"D:\T"), // renamed + source changed
                Input(0, "Added", @"C:\A", @"D:\A"),    // added
            ]);

            changes.Should().Contain("One way sync 'Gone' removed");
            changes.Should().Contain("One way sync 'Old' renamed to 'Renamed'");
            changes.Should().Contain(@"One way sync 'Renamed' source changed from 'C:\S' to 'C:\S2'");
            changes.Should().Contain(@"One way sync 'Added' added (C:\A -> D:\A)");
        }

        [Test]
        public void DescribeForCreateLog_ProducesDetailLinesPerPair()
        {
            var lines = _service.DescribeForCreateLog([Input(0, "A", @"C:\A", @"D:\A", allowDeletions: true, includeSubFolders: true)]);

            lines.Should().Contain("One way sync: A");
            lines.Should().Contain(@"Source: C:\A");
            lines.Should().Contain(@"Target: D:\A");
            lines.Should().Contain("Allow deletions: Yes");
            lines.Should().Contain("Include sub-folders: Yes");
            lines.Should().Contain(m => m.StartsWith("Overwrite:"));
        }

        [Test]
        public void DescribeForCreateLog_EmptyForNoPairs()
        {
            _service.DescribeForCreateLog([]).Should().BeEmpty();
        }
    }
}
