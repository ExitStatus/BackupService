using BackupService.FileSystem.Mtp;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class MtpDateStringTests
    {
        [Test]
        public void ParsesPtpDateString_NoFraction()
        {
            var result = MtpDateString.Parse("20260627T143015");

            result.Should().Be(new DateTime(2026, 6, 27, 14, 30, 15, DateTimeKind.Utc));
            result!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Test]
        public void ParsesPtpDateString_WithTenths()
        {
            MtpDateString.Parse("20260627T143015.0")
                .Should().Be(new DateTime(2026, 6, 27, 14, 30, 15, DateTimeKind.Utc));
        }

        [Test]
        public void ParsesPtpDateString_WithTrailingZ()
        {
            MtpDateString.Parse("20260627T143015Z")
                .Should().Be(new DateTime(2026, 6, 27, 14, 30, 15, DateTimeKind.Utc));
        }

        [Test]
        public void ParsesIsoVariant()
        {
            MtpDateString.Parse("2026-06-27T14:30:15")
                .Should().Be(new DateTime(2026, 6, 27, 14, 30, 15, DateTimeKind.Utc));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not a date")]
        [TestCase("0")]
        public void ReturnsNull_ForUnparseable(string? value)
        {
            MtpDateString.Parse(value).Should().BeNull();
        }
    }
}
