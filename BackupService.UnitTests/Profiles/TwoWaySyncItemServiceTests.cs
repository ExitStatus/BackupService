using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Profiles;
using FluentAssertions;

namespace BackupService.UnitTests.Profiles
{
    [TestFixture]
    public class TwoWaySyncItemServiceTests
    {
        private TwoWaySyncItemService _sut = null!;

        [SetUp]
        public void SetUp() => _sut = new TwoWaySyncItemService();

        private static TwoWaySyncInput Input(int id, string name, string source = @"C:\s", string target = @"C:\t",
            ConflictResolution conflict = ConflictResolution.NewerWins, bool propagateDeletions = true) =>
            new(id, name, source, target, IncludeSubFolders: false, conflict, propagateDeletions);

        [Test]
        public void Add_BuildsNewItemsOnTheProfile()
        {
            var profile = new Profile { Name = "P" };

            _sut.Add(profile, [Input(0, "A"), Input(0, "B")]);

            profile.TwoWaySyncItems.Select(i => i.Name).Should().BeEquivalentTo("A", "B");
        }

        [Test]
        public void Add_CarriesConflictAndDeletionSettings()
        {
            var profile = new Profile { Name = "P" };

            _sut.Add(profile, [Input(0, "A", conflict: ConflictResolution.KeepBoth, propagateDeletions: false)]);

            var item = profile.TwoWaySyncItems.Single();
            item.ConflictResolution.Should().Be(ConflictResolution.KeepBoth);
            item.PropagateDeletions.Should().BeFalse();
        }

        [Test]
        public void Sync_UpdatesMatchedById_AddsId0_AndRemovesTheRest()
        {
            var profile = new Profile { Name = "P" };
            profile.TwoWaySyncItems.Add(new TwoWaySyncItem { Id = 1, Name = "Keep", SourceFolder = @"C:\k", TargetFolder = @"C:\k2" });
            profile.TwoWaySyncItems.Add(new TwoWaySyncItem { Id = 2, Name = "Drop", SourceFolder = @"C:\d", TargetFolder = @"C:\d2" });

            var changes = _sut.Sync(profile, [Input(1, "Keep2"), Input(0, "New")]);

            profile.TwoWaySyncItems.Select(i => i.Name).Should().BeEquivalentTo("Keep2", "New");
            changes.Should().Contain(c => c.Contains("'Drop' removed"));
            changes.Should().Contain(c => c.Contains("'Keep' renamed to 'Keep2'"));
            changes.Should().Contain(c => c.Contains("'New' added"));
        }

        [Test]
        public void Sync_DescribesConflictAndDeletionChanges()
        {
            var profile = new Profile { Name = "P" };
            profile.TwoWaySyncItems.Add(new TwoWaySyncItem
            {
                Id = 1,
                Name = "A",
                SourceFolder = @"C:\s",
                TargetFolder = @"C:\t",
                ConflictResolution = ConflictResolution.NewerWins,
                PropagateDeletions = true,
            });

            var changes = _sut.Sync(profile, [Input(1, "A", conflict: ConflictResolution.SourceWins, propagateDeletions: false)]);

            changes.Should().Contain(c => c.Contains("conflict resolution changed"));
            changes.Should().Contain(c => c.Contains("propagate deletions changed"));
        }

        [Test]
        public void DescribeForCreateLog_EmitsPerItemLines()
        {
            var lines = _sut.DescribeForCreateLog([Input(0, "A", @"C:\src", @"C:\dst", ConflictResolution.KeepBoth, propagateDeletions: false)]);

            lines.Should().Contain("Two way sync: A");
            lines.Should().Contain(@"Source: C:\src");
            lines.Should().Contain(@"Target: C:\dst");
            lines.Should().Contain("Conflict resolution: Keep both");
            lines.Should().Contain("Propagate deletions: No");
        }
    }
}
