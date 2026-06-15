using BackupService.Enumerations;
using BackupService.Extensions;
using FluentAssertions;

namespace BackupService.UnitTests.Extensions
{
    [TestFixture]
    public class EnumExtensionsTests
    {
        private enum Sample
        {
            [System.ComponentModel.Description("A nice label")]
            Described,

            Plain,
        }

        [Test]
        public void GetDescription_ReturnsDescriptionAttributeText()
        {
            Sample.Described.GetDescription().Should().Be("A nice label");
        }

        [Test]
        public void GetDescription_FallsBackToNameWhenNoAttribute()
        {
            Sample.Plain.GetDescription().Should().Be("Plain");
        }

        [Test]
        public void GetDescription_ReadsProfileTypeDescription()
        {
            ProfileType.FolderPair.GetDescription().Should().Be("Folder Pairs");
        }
    }
}
