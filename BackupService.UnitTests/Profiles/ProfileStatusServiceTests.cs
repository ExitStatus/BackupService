using BackupService.Enumerations;
using BackupService.Profiles;
using FluentAssertions;

namespace BackupService.UnitTests.Profiles
{
    [TestFixture]
    public class ProfileStatusServiceTests
    {
        [Test]
        public void Get_DefaultsToIdleForUnknownProfile()
        {
            new ProfileStatusService().Get(42).Should().Be(ProfileStatus.Idle);
        }

        [Test]
        public void Set_UpdatesStatusAndRaisesChangedWithProfileId()
        {
            var service = new ProfileStatusService();
            int? raisedFor = null;
            service.Changed += id => raisedFor = id;

            service.Set(7, ProfileStatus.Running);

            service.Get(7).Should().Be(ProfileStatus.Running);
            raisedFor.Should().Be(7);
        }

        [Test]
        public void Remove_DropsTrackedStatusBackToDefault()
        {
            var service = new ProfileStatusService();
            service.Set(3, ProfileStatus.Error);

            service.Remove(3);

            service.Get(3).Should().Be(ProfileStatus.Idle);
        }

        [Test]
        public void Lock_ThenUnlock_TogglesIsLocked()
        {
            var service = new ProfileStatusService();
            service.IsLocked(9).Should().BeFalse();

            service.Lock(9);
            service.IsLocked(9).Should().BeTrue();

            service.Unlock(9);
            service.IsLocked(9).Should().BeFalse();
        }

        [Test]
        public void Remove_AlsoClearsLock()
        {
            var service = new ProfileStatusService();
            service.Lock(4);

            service.Remove(4);

            service.IsLocked(4).Should().BeFalse();
        }

        [Test]
        public void TryBeginRun_FirstCallBeginsAndSetsRunning()
        {
            var service = new ProfileStatusService();

            service.TryBeginRun(5).Should().BeTrue();
            service.IsRunning(5).Should().BeTrue();
            service.Get(5).Should().Be(ProfileStatus.Running);
        }

        [Test]
        public void TryBeginRun_WhenAlreadyRunningReturnsFalse()
        {
            var service = new ProfileStatusService();
            service.TryBeginRun(5).Should().BeTrue();

            service.TryBeginRun(5).Should().BeFalse();
        }

        [Test]
        public void TryBeginRun_AfterRunEndsCanBeginAgain()
        {
            var service = new ProfileStatusService();
            service.TryBeginRun(5).Should().BeTrue();

            service.Set(5, ProfileStatus.Idle);

            service.TryBeginRun(5).Should().BeTrue();
        }

        [Test]
        public void GetProgress_DefaultsToNull()
        {
            new ProfileStatusService().GetProgress(5).Should().BeNull();
        }

        [Test]
        public void SetProgress_StoresValueAndRaisesProgressChanged()
        {
            var service = new ProfileStatusService();
            int? raisedFor = null;
            service.ProgressChanged += id => raisedFor = id;

            service.SetProgress(5, 42);

            service.GetProgress(5).Should().Be(42);
            raisedFor.Should().Be(5);
        }

        [Test]
        public void SetProgress_ClampsTo0To100()
        {
            var service = new ProfileStatusService();

            service.SetProgress(5, -10);
            service.GetProgress(5).Should().Be(0);

            service.SetProgress(5, 150);
            service.GetProgress(5).Should().Be(100);
        }

        [Test]
        public void SetProgress_OnlyRaisesWhenTheIntegerValueChanges()
        {
            var service = new ProfileStatusService();
            var raised = 0;
            service.ProgressChanged += _ => raised++;

            service.SetProgress(5, 30);
            service.SetProgress(5, 30); // same value — no event
            service.SetProgress(5, 31);

            raised.Should().Be(2);
        }

        [Test]
        public void Set_ToNonRunning_ClearsProgress()
        {
            var service = new ProfileStatusService();
            service.SetProgress(5, 55);

            service.Set(5, ProfileStatus.Idle);

            service.GetProgress(5).Should().BeNull();
        }

        [Test]
        public void Set_ToRunning_LeavesProgressUntouched()
        {
            var service = new ProfileStatusService();
            service.SetProgress(5, 55);

            service.Set(5, ProfileStatus.Running);

            service.GetProgress(5).Should().Be(55);
        }
    }
}
