using BackupService.Enumerations;
using BackupService.Logging;
using FluentAssertions;
using Moq;

namespace BackupService.UnitTests.Logging
{
    [TestFixture]
    public class DeferredOperationLoggerTests
    {
        private Mock<IOperationLogFactory> _factory = null!;
        private Mock<IOperationLogger> _inner = null!;

        [SetUp]
        public void SetUp()
        {
            _inner = new Mock<IOperationLogger>();
            _factory = new Mock<IOperationLogFactory>();
            _factory
                .Setup(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_inner.Object);
        }

        private DeferredOperationLogger Sut() => new(_factory.Object, "Header", OperationLogLevel.Info, profileId: 7);

        [Test]
        public async Task NoWrites_NeverCreatesLog_AndSummaryIsNoOp()
        {
            var log = Sut();

            // No append/error calls at all — this models an all-no-op flush.
            await log.SetSummaryAsync("would-be summary", OperationLogLevel.Info);

            log.WasCreated.Should().BeFalse();
            _factory.Verify(f => f.CreateAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
            _inner.Verify(l => l.SetSummaryAsync(It.IsAny<string>(), It.IsAny<OperationLogLevel>()), Times.Never);
        }

        [Test]
        public async Task FirstAppend_CreatesLogOnce_AndForwards()
        {
            var log = Sut();

            await log.AppendAsync("line 1");
            await log.AppendAsync("line 2");

            log.WasCreated.Should().BeTrue();
            _factory.Verify(f => f.CreateAsync("Header", OperationLogLevel.Info, 7, It.IsAny<CancellationToken>()), Times.Once);
            _inner.Verify(l => l.AppendAsync(new[] { "line 1" }), Times.Once);
            _inner.Verify(l => l.AppendAsync(new[] { "line 2" }), Times.Once);
        }

        [Test]
        public async Task Error_CreatesLogAndForwards()
        {
            var log = Sut();
            var ex = new InvalidOperationException("boom");

            await log.ErrorAsync("failed", ex);

            log.WasCreated.Should().BeTrue();
            _inner.Verify(l => l.ErrorAsync("failed", ex), Times.Once);
        }

        [Test]
        public async Task SetSummary_AfterAWrite_ForwardsToInner()
        {
            var log = Sut();

            await log.AppendAsync("line");
            await log.SetSummaryAsync("final", OperationLogLevel.Warning);

            _inner.Verify(l => l.SetSummaryAsync("final", OperationLogLevel.Warning), Times.Once);
        }
    }
}
