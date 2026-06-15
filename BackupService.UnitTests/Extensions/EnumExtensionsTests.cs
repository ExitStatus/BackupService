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
            [HelpText("Some help")]
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

        [Test]
        public void GetHelpText_ReturnsHelpTextAttributeText()
        {
            Sample.Described.GetHelpText().Should().Be("Some help");
        }

        [Test]
        public void GetHelpText_IsEmptyWhenNoAttribute()
        {
            Sample.Plain.GetHelpText().Should().BeEmpty();
        }

        [Test]
        public void GetHelpText_ReadsOverwriteBehaviourHelpText()
        {
            OverwriteBehaviour.DoNotOverwriteNewer.GetHelpText()
                .Should().Be("Any file at the destination with a newer date will not be overwritten");
        }
    }
}
