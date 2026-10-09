using BackupService.ScheduledTasks;
using FluentAssertions;

namespace BackupService.UnitTests.ScheduledTasks
{
    [TestFixture]
    public class ScheduledTaskStatusServiceTests
    {
        [Test]
        public void ALockHeldInTwoPlaces_StaysUntilBothRelease()
        {
            // Two tabs editing the same task: one closing mustn't let a scheduled run fire under the other.
            var service = new ScheduledTaskStatusService();
            service.Lock(3);
            service.Lock(3);

            service.Unlock(3);
            service.IsLocked(3).Should().BeTrue();

            service.Unlock(3);
            service.IsLocked(3).Should().BeFalse();
        }

        [Test]
        public void UnlockingATaskThatIsntLocked_DoesNothing()
        {
            var service = new ScheduledTaskStatusService();

            service.Unlock(3);

            service.IsLocked(3).Should().BeFalse();
        }
    }
}
