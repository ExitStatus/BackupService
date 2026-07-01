using BackupService.Scheduling.Usb;
using FluentAssertions;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class UsbRunGateTests
    {
        [Test]
        public async Task EmptySet_IsNoOp()
        {
            var gate = new UsbRunGate();

            await using var handle = await gate.AcquireAsync([], CancellationToken.None);

            handle.Should().NotBeNull();
        }

        [Test]
        public async Task SameDevice_SecondAcquireWaitsUntilFirstReleases()
        {
            var gate = new UsbRunGate();
            var first = await gate.AcquireAsync([1], CancellationToken.None);

            var second = gate.AcquireAsync([1], CancellationToken.None);
            await Task.Delay(50);
            second.IsCompleted.Should().BeFalse("the device is held by the first run");

            await first.DisposeAsync();
            var secondHandle = await second; // now proceeds
            await secondHandle.DisposeAsync();
        }

        [Test]
        public async Task DifferentDevices_DoNotBlockEachOther()
        {
            var gate = new UsbRunGate();
            await using var a = await gate.AcquireAsync([1], CancellationToken.None);

            var b = await gate.AcquireAsync([2], CancellationToken.None); // different device — no wait
            b.Should().NotBeNull();
            await b.DisposeAsync();
        }

        [Test]
        public async Task Cancellation_WhileWaiting_Throws_AndLeavesTheDeviceUsable()
        {
            var gate = new UsbRunGate();
            var first = await gate.AcquireAsync([1], CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var second = gate.AcquireAsync([1], cts.Token);
            cts.Cancel();

            Func<Task> act = async () => await second;
            await act.Should().ThrowAsync<OperationCanceledException>();

            await first.DisposeAsync();
            await using var third = await gate.AcquireAsync([1], CancellationToken.None);
            third.Should().NotBeNull();
        }

        [Test]
        public async Task Cancellation_ReleasesGatesAlreadyTaken()
        {
            var gate = new UsbRunGate();
            await using var holdTwo = await gate.AcquireAsync([2], CancellationToken.None);

            using var cts = new CancellationTokenSource();
            var multi = gate.AcquireAsync([1, 2], cts.Token); // acquires 1 (free), then waits on 2 (held)
            await Task.Delay(50);
            cts.Cancel();

            Func<Task> act = async () => await multi;
            await act.Should().ThrowAsync<OperationCanceledException>();

            // Device 1 must have been released by the cancellation cleanup — acquiring it must not block.
            await using var takeOne = await gate.AcquireAsync([1], CancellationToken.None);
            takeOne.Should().NotBeNull();
        }
    }
}
