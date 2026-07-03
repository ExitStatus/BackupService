using BackupService.Scheduling.Groups;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class GroupRunGateTests
    {
        [Test]
        public async Task NullGroup_IsNoOp()
        {
            var gate = new GroupRunGate();

            await using var handle = await gate.AcquireAsync(null, CancellationToken.None);

            handle.Should().NotBeNull();
        }

        [Test]
        public async Task NullGroup_ConcurrentAcquires_DoNotBlock()
        {
            var gate = new GroupRunGate();

            await using var first = await gate.AcquireAsync(null, CancellationToken.None);
            // A null id acquires nothing, so a second concurrent acquire proceeds immediately.
            var second = gate.AcquireAsync(null, CancellationToken.None);

            second.IsCompleted.Should().BeTrue();
            await (await second).DisposeAsync();
        }

        [Test]
        public async Task SameSequentialGroup_SecondAcquireWaitsUntilFirstReleases()
        {
            var gate = new GroupRunGate();
            var first = await gate.AcquireAsync(1, CancellationToken.None);

            var second = gate.AcquireAsync(1, CancellationToken.None);
            await Task.Delay(50);
            second.IsCompleted.Should().BeFalse("the group is held by the first run");

            await first.DisposeAsync();
            var secondHandle = await second; // now proceeds
            await secondHandle.DisposeAsync();
        }

        [Test]
        public async Task DifferentGroups_DoNotBlockEachOther()
        {
            var gate = new GroupRunGate();
            await using var a = await gate.AcquireAsync(1, CancellationToken.None);

            var b = await gate.AcquireAsync(2, CancellationToken.None); // different group — no wait
            b.Should().NotBeNull();
            await b.DisposeAsync();
        }

        [Test]
        public async Task Release_IsIdempotent()
        {
            var gate = new GroupRunGate();
            var handle = await gate.AcquireAsync(1, CancellationToken.None);

            await handle.DisposeAsync();
            await handle.DisposeAsync(); // second dispose is a no-op, doesn't over-release

            // The group must be free exactly once — acquiring it doesn't block, and a further acquire waits.
            var again = await gate.AcquireAsync(1, CancellationToken.None);
            var blocked = gate.AcquireAsync(1, CancellationToken.None);
            await Task.Delay(50);
            blocked.IsCompleted.Should().BeFalse("the group is held again; the double-dispose didn't leak a permit");

            await again.DisposeAsync();
            await (await blocked).DisposeAsync();
        }

        [Test]
        public async Task Cancellation_WhileWaiting_Throws_AndLeavesTheGroupUsable()
        {
            var gate = new GroupRunGate();
            var first = await gate.AcquireAsync(1, CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var second = gate.AcquireAsync(1, cts.Token);
            cts.Cancel();

            Func<Task> act = async () => await second;
            await act.Should().ThrowAsync<OperationCanceledException>();

            await first.DisposeAsync();
            await using var third = await gate.AcquireAsync(1, CancellationToken.None);
            third.Should().NotBeNull();
        }
    }
}
