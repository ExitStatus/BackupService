using BackupService.Connections.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class UsbDeviceMatcherTests
    {
        [Test]
        public void Matches_WhenBothHaveHardwareSerial_RequiresHardwareEquality()
        {
            // Hardware serials equal → match, even with different volume serials (e.g. after a reformat).
            UsbDeviceMatcher.Matches("HW1", "VOL-A", "HW1", "VOL-B").Should().BeTrue();
        }

        [Test]
        public void Matches_WhenBothHaveHardwareSerial_DiffersDespiteEqualVolume()
        {
            // Different hardware serials → no match even if the volume serials happen to be equal.
            UsbDeviceMatcher.Matches("HW1", "VOL", "HW2", "VOL").Should().BeFalse();
        }

        [Test]
        public void Matches_WhenConnectorHasNoHardwareSerial_FallsBackToVolume()
        {
            UsbDeviceMatcher.Matches(null, "VOL", "HW2", "VOL").Should().BeTrue();
            UsbDeviceMatcher.Matches(null, "VOL-A", "HW2", "VOL-B").Should().BeFalse();
        }

        [Test]
        public void Matches_WhenDeviceHasNoHardwareSerial_FallsBackToVolume()
        {
            UsbDeviceMatcher.Matches("HW1", "VOL", null, "VOL").Should().BeTrue();
            UsbDeviceMatcher.Matches("HW1", "VOL-A", null, "VOL-B").Should().BeFalse();
        }

        [Test]
        public void Matches_IsCaseInsensitive()
        {
            UsbDeviceMatcher.Matches("hw1", "vol", "HW1", "VOL").Should().BeTrue();
        }

        [Test]
        public void Matches_FalseWhenVolumeSerialIsEmptyOnFallback()
        {
            UsbDeviceMatcher.Matches(null, string.Empty, null, string.Empty).Should().BeFalse();
        }

        [Test]
        public void Matches_UsingInfoAndDeviceOverload()
        {
            var connection = new UsbConnectionInfo(BackupService.Enumerations.UsbDeviceKind.MassStorage, HardwareSerial: "HW1", VolumeSerial: "VOL", MtpSerial: null, RootFolder: null);
            var device = new UsbDevice("D:", @"D:\", "Camera", "VOL", "HW1");

            UsbDeviceMatcher.Matches(connection, device).Should().BeTrue();
        }

        [Test]
        public void MatchesMtp_EqualSerials_Match_CaseInsensitive()
        {
            UsbDeviceMatcher.MatchesMtp("mtp-serial-1", "MTP-SERIAL-1").Should().BeTrue();
        }

        [Test]
        public void MatchesMtp_DifferentSerials_DoNotMatch()
        {
            UsbDeviceMatcher.MatchesMtp("camera-A", "camera-B").Should().BeFalse();
        }

        [Test]
        public void MatchesMtp_NullOrEmptyConnectorSerial_DoesNotMatch()
        {
            UsbDeviceMatcher.MatchesMtp(null, "x").Should().BeFalse();
            UsbDeviceMatcher.MatchesMtp(string.Empty, "x").Should().BeFalse();
        }
    }
}
