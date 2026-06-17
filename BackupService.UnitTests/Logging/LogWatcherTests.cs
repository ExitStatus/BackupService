using BackupService.Logging;
using FluentAssertions;

namespace BackupService.UnitTests.Logging
{
    [TestFixture]
    public class LogWatcherTests
    {
        // Generous relative to the watcher's ~500ms debounce so the assertions aren't timing-flaky.
        private static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(1500);

        [Test]
        public async Task Notify_CoalescesABurstIntoASingleChanged()
        {
            using var watcher = new LogWatcher();
            var count = 0;
            watcher.Changed += () => Interlocked.Increment(ref count);

            for (var i = 0; i < 50; i++)
            {
                watcher.Notify();
            }

            await Task.Delay(SettleWait);

            count.Should().Be(1);
        }

        [Test]
        public async Task Notify_AfterAWindowSettles_FiresAgain()
        {
            using var watcher = new LogWatcher();
            var count = 0;
            watcher.Changed += () => Interlocked.Increment(ref count);

            watcher.Notify();
            await Task.Delay(SettleWait);

            watcher.Notify();
            await Task.Delay(SettleWait);

            count.Should().Be(2);
        }

        [Test]
        public async Task Changed_InvokesAllSubscribers()
        {
            using var watcher = new LogWatcher();
            var a = 0;
            var b = 0;
            watcher.Changed += () => Interlocked.Increment(ref a);
            watcher.Changed += () => Interlocked.Increment(ref b);

            watcher.Notify();
            await Task.Delay(SettleWait);

            a.Should().Be(1);
            b.Should().Be(1);
        }

        [Test]
        public async Task Changed_ContinuesWhenOneSubscriberThrows()
        {
            using var watcher = new LogWatcher();
            var reached = 0;
            watcher.Changed += () => throw new InvalidOperationException("bad subscriber");
            watcher.Changed += () => Interlocked.Increment(ref reached);

            watcher.Notify();
            await Task.Delay(SettleWait);

            reached.Should().Be(1); // the throwing handler didn't stop the next one
        }

        [Test]
        public async Task Notify_AfterDispose_DoesNotFire()
        {
            var watcher = new LogWatcher();
            var count = 0;
            watcher.Changed += () => Interlocked.Increment(ref count);
            watcher.Dispose();

            watcher.Notify();
            await Task.Delay(SettleWait);

            count.Should().Be(0);
        }
    }
}
