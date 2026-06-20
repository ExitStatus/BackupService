using BackupService.Enumerations;
using BackupService.FileSystem;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class BackupFilterTests
    {
        private static BackupFilter Filter(params FilterRule[] rules) => new(rules);

        private static FilterRule Include(string pattern) => new(FilterDirection.Include, FilterKind.File, pattern);
        private static FilterRule ExcludeFile(string pattern) => new(FilterDirection.Exclude, FilterKind.File, pattern);
        private static FilterRule ExcludeFolder(string pattern) => new(FilterDirection.Exclude, FilterKind.Folder, pattern);
        private static FilterRule ExcludePath(string pattern) => new(FilterDirection.Exclude, FilterKind.Path, pattern);

        [Test]
        public void NoRules_IsEmpty_AndEverythingInScope()
        {
            var filter = Filter();

            filter.IsEmpty.Should().BeTrue();
            filter.IncludesFile("anything.dat").Should().BeTrue();
            filter.IsFileInScope("anything.dat", []).Should().BeTrue();
        }

        [Test]
        public void Includes_RestrictToMatchingNames()
        {
            var filter = Filter(Include("*.txt"));

            filter.IsEmpty.Should().BeFalse();
            filter.IncludesFile("notes.txt").Should().BeTrue();
            filter.IncludesFile("data.bin").Should().BeFalse();
            filter.IsFileInScope("notes.txt", ["sub"]).Should().BeTrue(); // name-only: any folder
            filter.IsFileInScope("data.bin", []).Should().BeFalse();
        }

        [Test]
        public void Includes_LiteralFile_MatchesThatNameOnly()
        {
            var filter = Filter(Include("report.docx"));

            filter.IncludesFile("report.docx").Should().BeTrue();
            filter.IncludesFile("report.docx.bak").Should().BeFalse();
            filter.IncludesFile("other.docx").Should().BeFalse();
        }

        [Test]
        public void ExcludeFile_DropsMatching_OthersStayWhenNoIncludes()
        {
            var filter = Filter(ExcludeFile("*.tmp"));

            filter.IsFileInScope("build.tmp", []).Should().BeFalse();
            filter.IsFileInScope("keep.txt", []).Should().BeTrue();
        }

        [Test]
        public void ExcludeFolder_SkipsTheWholeSubtree()
        {
            var filter = Filter(ExcludeFolder("bin"));

            filter.ExcludesFolder("bin").Should().BeTrue();
            filter.ExcludesFolder("src").Should().BeFalse();
            filter.IsFileInScope("a.txt", ["bin"]).Should().BeFalse();
            filter.IsFileInScope("a.txt", ["src", "bin"]).Should().BeFalse(); // any ancestor
            filter.IsFileInScope("a.txt", ["src"]).Should().BeTrue();
        }

        [Test]
        public void IsRelativePathInScope_SplitsPath_BothSeparators()
        {
            var filter = Filter(ExcludeFolder("bin"), ExcludeFile("*.tmp"));

            filter.IsRelativePathInScope(@"bin\a.txt").Should().BeFalse();   // excluded folder
            filter.IsRelativePathInScope("sub/x.tmp").Should().BeFalse();    // excluded file
            filter.IsRelativePathInScope(@"src\keep.txt").Should().BeTrue();
        }

        [Test]
        public void Matching_IsCaseInsensitive()
        {
            var filter = Filter(Include("*.TXT"), ExcludeFolder("BIN"));

            filter.IncludesFile("readme.txt").Should().BeTrue();
            filter.ExcludesFolder("bin").Should().BeTrue();
        }

        [Test]
        public void IncludesAndExcludes_Combine_ExcludeWins()
        {
            var filter = Filter(Include("*.txt"), ExcludeFile("secret.txt"));

            filter.IsFileInScope("notes.txt", []).Should().BeTrue();
            filter.IsFileInScope("secret.txt", []).Should().BeFalse(); // matches include but excluded
        }

        [Test]
        public void ExcludePath_DropsThatExactLocation_NotByNameElsewhere()
        {
            var filter = Filter(ExcludePath(@"bin\Debug"));

            filter.IsEmpty.Should().BeFalse();
            // The exact relative path (the folder itself and anything beneath it) is excluded.
            filter.ExcludesPath(["bin", "Debug"]).Should().BeTrue();
            filter.IsFileInScope("app.dll", ["bin", "Debug"]).Should().BeFalse();
            filter.IsFileInScope("app.dll", ["bin", "Debug", "net10.0"]).Should().BeFalse(); // subtree
            // A folder of the same name at a different location is NOT excluded (unlike an exclude-Folder).
            filter.IsFileInScope("app.dll", ["src", "bin", "Debug"]).Should().BeTrue();
            filter.ExcludesFolder("Debug").Should().BeFalse();
        }

        [Test]
        public void ExcludePath_CanTargetASingleFile()
        {
            var filter = Filter(ExcludePath(@"config\secrets.json"));

            filter.IsFileInScope("secrets.json", ["config"]).Should().BeFalse();
            filter.IsFileInScope("secrets.json", ["other"]).Should().BeTrue();   // different location
            filter.IsFileInScope("settings.json", ["config"]).Should().BeTrue(); // different file
        }

        [Test]
        public void ExcludePath_NormalisesSeparators_AndIsCaseInsensitive()
        {
            var filter = Filter(ExcludePath("Logs/Old"));

            filter.IsRelativePathInScope(@"logs\old\a.txt").Should().BeFalse();
            filter.IsRelativePathInScope(@"logs\current\a.txt").Should().BeTrue();
        }

        [Test]
        public void BlankPatterns_AreIgnored()
        {
            var filter = Filter(Include("   "), ExcludeFile(""));

            filter.IsEmpty.Should().BeTrue();
        }
    }
}
