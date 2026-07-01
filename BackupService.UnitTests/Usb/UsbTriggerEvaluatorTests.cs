using BackupService.Scheduling.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class UsbTriggerEvaluatorTests
    {
        // Connection ids: 1 = USB (source), 2 = USB (target), 3 = non-USB (e.g. SMB).
        private static bool IsUsb(int id) => id is 1 or 2;

        private static Func<int, bool> Connected(params int[] connectedIds) => id => connectedIds.Contains(id);

        [Test]
        public void NoConnections_IsAllowed()
        {
            UsbTriggerEvaluator.AllRequiredUsbConnected(null, null, IsUsb, Connected()).Should().BeTrue();
        }

        [Test]
        public void NonUsbSides_NeedNoDevice()
        {
            // Source local (null), target SMB (3, not USB) — nothing to wait for.
            UsbTriggerEvaluator.AllRequiredUsbConnected(null, 3, IsUsb, Connected()).Should().BeTrue();
        }

        [Test]
        public void UsbSource_RequiresThatDevice()
        {
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, null, IsUsb, Connected()).Should().BeFalse();
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, null, IsUsb, Connected(1)).Should().BeTrue();
        }

        [Test]
        public void UsbTarget_RequiresThatDevice()
        {
            // Source local, target USB.
            UsbTriggerEvaluator.AllRequiredUsbConnected(null, 2, IsUsb, Connected()).Should().BeFalse();
            UsbTriggerEvaluator.AllRequiredUsbConnected(null, 2, IsUsb, Connected(2)).Should().BeTrue();
        }

        [Test]
        public void BothUsb_RequireBothPluggedIn()
        {
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, 2, IsUsb, Connected(1)).Should().BeFalse();
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, 2, IsUsb, Connected(2)).Should().BeFalse();
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, 2, IsUsb, Connected(1, 2)).Should().BeTrue();
        }

        [Test]
        public void UsbSourceWithNonUsbTarget_OnlyWaitsForTheUsbDevice()
        {
            UsbTriggerEvaluator.AllRequiredUsbConnected(1, 3, IsUsb, Connected(1)).Should().BeTrue();
        }
    }
}
