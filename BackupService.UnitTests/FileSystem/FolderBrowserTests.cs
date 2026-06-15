using BackupService.FileSystem;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class FolderBrowserTests
    {
        private string _root = null!;
        private FolderBrowser _browser = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "fbtests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _browser = new FolderBrowser();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Test]
        public void GetDirectories_ReturnsEntriesNameAscendingWithDates()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Beta"));
            Directory.CreateDirectory(Path.Combine(_root, "Alpha"));

            var entries = _browser.GetDirectories(_root);

            entries.Select(e => e.Name).Should().ContainInOrder("Alpha", "Beta");
            entries.Should().OnlyContain(e => e.DateModified != null);
            entries.First(e => e.Name == "Alpha").FullPath
                .Should().Be(Path.Combine(_root, "Alpha"));
        }

        [Test]
        public void GetDirectories_ReturnsEmptyForMissingPath()
        {
            _browser.GetDirectories(Path.Combine(_root, "does-not-exist"))
                .Should().BeEmpty();
        }

        [Test]
        public void GetDrives_ReturnsLabelledDrives()
        {
            var drives = _browser.GetDrives();

            drives.Should().NotBeEmpty();
            drives.Should().OnlyContain(d => d.Label.EndsWith(")") && d.Label.Contains("("));
            drives.Should().Contain(d => d.RootPath == Path.GetPathRoot(_root));
        }

        [Test]
        public void GetQuickAccess_ReturnsOnlyExistingFolders()
        {
            var quick = _browser.GetQuickAccess();

            quick.Should().OnlyContain(e => Directory.Exists(e.FullPath));
        }
    }
}
