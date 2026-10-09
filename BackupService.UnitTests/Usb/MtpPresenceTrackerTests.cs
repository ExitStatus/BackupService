using BackupService.Connections.Usb;
using BackupService.Scheduling.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class MtpPresenceTrackerTests
    {
        private static readonly MtpDevice Camera = new("USB#VID_054C&PID_0994#CAM1", "Camera");
        private static readonly MtpDevice Phone = new("USB#VID_18D1&PID_4EE1#PHONE1", "Phone");

        [Test]
        public void DevicesConnectedAtStartup_AreNotArrivals()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);

            var (arrived, removed) = tracker.Update([Camera]);

            arrived.Should().BeEmpty();
            removed.Should().BeEmpty();
        }

        [Test]
        public void ANewDevice_ArrivesOnce_HoweverManyScansSeeIt()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);

            tracker.Update([Camera, Phone]).Arrived.Should().ContainSingle().Which.Should().Be(Phone);
            tracker.Update([Camera, Phone]).Arrived.Should().BeEmpty();
            tracker.Update([Phone, Camera]).Arrived.Should().BeEmpty();
        }

        [Test]
        public void ADeviceMissingFromOneScan_IsNotYetRemoved()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);

            var (_, removed) = tracker.Update([]);

            removed.Should().BeEmpty();
            tracker.HasUnconfirmedRemovals.Should().BeTrue("another scan is needed to settle it");
        }

        [Test]
        public void ADeviceMissingFromTwoScansInARow_IsRemoved()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera, Phone]);

            tracker.Update([Phone]);
            var (_, removed) = tracker.Update([Phone]);

            removed.Should().Equal(Camera.Serial);
            tracker.HasUnconfirmedRemovals.Should().BeFalse();
        }

        [Test]
        public void ADeviceThatBlinksOutOfTheList_IsNeitherRemovedNorArrivesAgain()
        {
            // Treating the blip as an unplug would log a disconnect and, on the next scan, an arrival that runs the
            // device's profiles a second time.
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);

            var gone = tracker.Update([]);
            var back = tracker.Update([Camera]);

            gone.Removed.Should().BeEmpty();
            back.Arrived.Should().BeEmpty();
            back.Removed.Should().BeEmpty();
            tracker.HasUnconfirmedRemovals.Should().BeFalse();
        }

        [Test]
        public void ADeviceReplugged_AfterItWasRemoved_ArrivesAgain()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);
            tracker.Update([]);
            tracker.Update([]);

            tracker.Update([Camera]).Arrived.Should().ContainSingle().Which.Should().Be(Camera);
        }

        [Test]
        public void Serials_AreMatchedIgnoringCase_AndADuplicateInAScanCountsOnce()
        {
            var tracker = new MtpPresenceTracker();
            tracker.Seed([Camera]);
            var sameCameraDifferentCase = Camera with { Serial = Camera.Serial.ToLowerInvariant() };

            var (arrived, removed) = tracker.Update([sameCameraDifferentCase, Phone, Phone]);

            arrived.Should().ContainSingle().Which.Should().Be(Phone);
            removed.Should().BeEmpty();
        }
    }
}
