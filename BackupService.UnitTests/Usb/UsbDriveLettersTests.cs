using BackupService.Connections.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class UsbDriveLettersTests
    {
        [Test]
        public void FromUnitMask_DecodesSingleDrive()
        {
            // Bit 3 set → D:
            UsbDriveLetters.FromUnitMask(1u << 3).Should().Equal("D:");
        }

        [Test]
        public void FromUnitMask_DecodesFirstAndLastDrives()
        {
            UsbDriveLetters.FromUnitMask(1u).Should().Equal("A:");
            UsbDriveLetters.FromUnitMask(1u << 25).Should().Equal("Z:");
        }

        [Test]
        public void FromUnitMask_DecodesMultipleDrives()
        {
            // Bits 3 and 5 → D: and F:
            UsbDriveLetters.FromUnitMask((1u << 3) | (1u << 5)).Should().Equal("D:", "F:");
        }

        [Test]
        public void FromUnitMask_EmptyMaskIsEmpty()
        {
            UsbDriveLetters.FromUnitMask(0).Should().BeEmpty();
        }
    }
}
