using System.IO;
using BackupService.FileSystem;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    /// <summary>
    /// Light integration coverage for the real <see cref="BackupFileSystem"/> against a temp folder —
    /// confirms the System.IO behaviours the sync engine relies on (timestamp-preserving copy, content
    /// compare, rename).
    /// </summary>
    [TestFixture]
    public class BackupFileSystemTests
    {
        private string _root = null!;
        private BackupFileSystem _fs = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "BackupServiceTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _fs = new BackupFileSystem();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private string Path2(string name) => Path.Combine(_root, name);

        [Test]
        public void CopyFile_PreservesLastWriteTime()
        {
            var source = Path2("source.txt");
            var dest = Path2("dest.txt");
            File.WriteAllText(source, "hello");
            var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, stamp);

            _fs.CopyFile(source, dest, overwrite: false);

            _fs.FileExists(dest).Should().BeTrue();
            _fs.GetLastWriteTimeUtc(dest).Should().Be(stamp); // skip-unchanged logic depends on this
            File.ReadAllText(dest).Should().Be("hello");
        }

        [Test]
        public void FilesContentEqual_TrueForIdentical_FalseForDifferent()
        {
            var a = Path2("a.txt");
            var b = Path2("b.txt");
            var c = Path2("c.txt");
            File.WriteAllText(a, "same content");
            File.WriteAllText(b, "same content");
            File.WriteAllText(c, "different");

            _fs.FilesContentEqual(a, b).Should().BeTrue();
            _fs.FilesContentEqual(a, c).Should().BeFalse();
        }

        [Test]
        public void MoveFile_RenamesSourceToDestination()
        {
            var source = Path2("temp.tmp");
            var dest = Path2("final.txt");
            File.WriteAllText(source, "data");

            _fs.MoveFile(source, dest, overwrite: false);

            _fs.FileExists(source).Should().BeFalse();
            _fs.FileExists(dest).Should().BeTrue();
            File.ReadAllText(dest).Should().Be("data");
        }

        [Test]
        public void GetFiles_And_GetDirectories_ReturnTopLevelEntries()
        {
            File.WriteAllText(Path2("one.txt"), "1");
            File.WriteAllText(Path2("two.txt"), "2");
            Directory.CreateDirectory(Path2("sub"));
            File.WriteAllText(Path.Combine(_root, "sub", "nested.txt"), "n");

            _fs.GetFiles(_root).Select(Path.GetFileName).Should().BeEquivalentTo("one.txt", "two.txt");
            _fs.GetDirectories(_root).Select(Path.GetFileName).Should().BeEquivalentTo("sub");
        }
    }
}
