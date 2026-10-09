using BackupService.Scheduling.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class RescanCoalescerTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Test]
        public async Task RequestsDuringAScan_RunOneMoreScanAfterIt()
        {
            // A device that registers while a scan sequence is under way must still be looked for.
            var runs = 0;
            var firstStarted = new TaskCompletionSource();
            var releaseFirst = new TaskCompletionSource();
            var sut = new RescanCoalescer(async () =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            });

            sut.Request();
            await firstStarted.Task.WaitAsync(Timeout);
            sut.Request();
            sut.Request();
            sut.Request();
            releaseFirst.SetResult();

            await WaitUntilAsync(() => Volatile.Read(ref runs) >= 2);
            await Task.Delay(200);
            runs.Should().Be(2, "the requests made during the first scan fold into one more");
        }

        [Test]
        public async Task OnlyOneScanRunsAtATime()
        {
            var running = 0;
            var maxRunning = 0;
            var runs = 0;
            var sut = new RescanCoalescer(async () =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(30);
                Interlocked.Decrement(ref running);
                Interlocked.Increment(ref runs);
            });

            for (var i = 0; i < 20; i++)
            {
                sut.Request();
                await Task.Delay(5);
            }

            await WaitUntilAsync(() => Volatile.Read(ref running) == 0 && Volatile.Read(ref runs) > 0);
            await Task.Delay(200);
            maxRunning.Should().Be(1);
        }

        [Test]
        public async Task AFailingScan_DoesNotStopLaterRequests()
        {
            var runs = 0;
            var sut = new RescanCoalescer(() =>
            {
                Interlocked.Increment(ref runs);
                throw new InvalidOperationException("scan failed");
            });

            sut.Request();
            await WaitUntilAsync(() => Volatile.Read(ref runs) == 1);
            await Task.Delay(100);
            sut.Request();

            await WaitUntilAsync(() => Volatile.Read(ref runs) == 2);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Timed out waiting for the scan.");
                }
                await Task.Delay(10);
            }
        }
    }
}
