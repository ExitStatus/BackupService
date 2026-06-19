using BackupService.Components.Controls;
using BackupService.Enumerations;
using FluentAssertions;

namespace BackupService.UnitTests.Components
{
    [TestFixture]
    public class FilterValidationTests
    {
        private static FilterEntryModel Entry(FilterKind kind, string pattern) => new() { Kind = kind, Pattern = pattern };

        [Test]
        public void CleanEntry_IsAllowed()
        {
            var error = FilterValidation.Validate(FilterKind.File, "*.txt", [], []);

            error.Should().BeNull();
        }

        [Test]
        public void Empty_IsRejected()
        {
            FilterValidation.Validate(FilterKind.File, "   ", [], []).Should().NotBeNull();
        }

        [Test]
        public void Duplicate_OnSameTab_IsRejected()
        {
            var sameTab = new List<FilterEntryModel> { Entry(FilterKind.File, "*.txt") };

            var error = FilterValidation.Validate(FilterKind.File, "*.txt", sameTab, []);

            error.Should().Contain("already in this list");
        }

        [Test]
        public void SameValue_OnOtherTab_IsRejected()
        {
            var otherTab = new List<FilterEntryModel> { Entry(FilterKind.Folder, "bin") };

            var error = FilterValidation.Validate(FilterKind.File, "bin", [], otherTab);

            error.Should().Contain("already on the other tab");
        }

        [Test]
        public void Literal_CoveredByExistingWildcard_IsRejected()
        {
            var sameTab = new List<FilterEntryModel> { Entry(FilterKind.File, "*.tmp") };

            var error = FilterValidation.Validate(FilterKind.File, "a.tmp", sameTab, []);

            error.Should().Contain("already covered by '*.tmp'");
        }

        [Test]
        public void Wildcard_OverExistingLiteral_IsAllowed()
        {
            // The redundancy rule only blocks a literal under a wildcard, not the reverse.
            var sameTab = new List<FilterEntryModel> { Entry(FilterKind.File, "a.tmp") };

            var error = FilterValidation.Validate(FilterKind.File, "*.tmp", sameTab, []);

            error.Should().BeNull();
        }

        [Test]
        public void Comparisons_AreCaseInsensitive()
        {
            var sameTab = new List<FilterEntryModel> { Entry(FilterKind.File, "Notes.TXT") };

            FilterValidation.Validate(FilterKind.File, "notes.txt", sameTab, []).Should().Contain("already in this list");
        }
    }
}
