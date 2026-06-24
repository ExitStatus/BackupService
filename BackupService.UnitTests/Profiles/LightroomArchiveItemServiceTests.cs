using BackupService.Database;
using BackupService.Profiles;
using FluentAssertions;

namespace BackupService.UnitTests.Profiles
{
    [TestFixture]
    public class LightroomArchiveItemServiceTests
    {
        private LightroomArchiveItemService _sut = null!;

        [SetUp]
        public void SetUp() => _sut = new LightroomArchiveItemService();

        private static LightroomArchiveInput Input(int id, string name, string source = @"C:\s", string target = @"C:\t") =>
            new(id, name, source, target, DebounceMilliseconds: 1000, IncludeSubFolders: false, AllowDeletions: false);

        [Test]
        public void Add_BuildsNewItemsOnTheProfile()
        {
            var profile = new Profile { Name = "P" };

            _sut.Add(profile, [Input(0, "A"), Input(0, "B")]);

            profile.LightroomArchiveItems.Select(i => i.Name).Should().BeEquivalentTo("A", "B");
            profile.LightroomArchiveItems.Should().OnlyContain(i => i.TargetConnectionId == null);
        }

        [Test]
        public void Sync_UpdatesMatchedById_AddsId0_AndRemovesTheRest()
        {
            var profile = new Profile { Name = "P" };
            profile.LightroomArchiveItems.Add(new LightroomArchiveItem { Id = 1, Name = "Keep", SourceFolder = @"C:\k", TargetFolder = @"C:\k2" });
            profile.LightroomArchiveItems.Add(new LightroomArchiveItem { Id = 2, Name = "Drop", SourceFolder = @"C:\d", TargetFolder = @"C:\d2" });

            var changes = _sut.Sync(profile, [Input(1, "Keep2"), Input(0, "New")]);

            profile.LightroomArchiveItems.Select(i => i.Name).Should().BeEquivalentTo("Keep2", "New");
            changes.Should().Contain(c => c.Contains("'Drop' removed"));
            changes.Should().Contain(c => c.Contains("'Keep' renamed to 'Keep2'"));
            changes.Should().Contain(c => c.Contains("'New' added"));
        }

        [Test]
        public void DescribeForCreateLog_EmitsPerItemLines()
        {
            var lines = _sut.DescribeForCreateLog([Input(0, "A", @"C:\src", @"C:\dst")]);

            lines.Should().Contain("Lightroom archive: A");
            lines.Should().Contain(@"Source: C:\src");
            lines.Should().Contain(@"Target: C:\dst");
        }
    }
}
