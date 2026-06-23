using BackupService.Extensions;
using FluentAssertions;

namespace BackupService.UnitTests.Extensions
{
    [TestFixture]
    public class ByteSizeTests
    {
        [TestCase(0L, "0 B")]
        [TestCase(512L, "512 B")]
        [TestCase(1023L, "1023 B")]
        [TestCase(1024L, "1.0 KB")]
        [TestCase(1536L, "1.5 KB")]                       // < 10 ⇒ one decimal
        [TestCase(10L * 1024, "10 KB")]                   // ≥ 10 ⇒ no decimals
        [TestCase(1024L * 1024, "1.0 MB")]
        [TestCase(5L * 1024 * 1024 * 1024, "5.0 GB")]
        [TestCase(2L * 1024 * 1024 * 1024 * 1024, "2.0 TB")]
        public void Humanize_ScalesAndFormats(long bytes, string expected) =>
            ByteSize.Humanize(bytes).Should().Be(expected);

        [Test]
        public void Humanize_Nullable_ReturnsDashForNull() =>
            ByteSize.Humanize((long?)null).Should().Be("—");

        [Test]
        public void Humanize_Nullable_FormatsValue() =>
            ByteSize.Humanize((long?)2048).Should().Be("2.0 KB");
    }
}
