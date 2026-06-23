using BackupService.Connections;
using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Smb;
using BackupService.Enumerations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Connections
{
    [TestFixture]
    public class ConnectionSpaceServiceTests
    {
        private Mock<IConnectionResolver> _resolver = null!;
        private Mock<ISmbConnector> _smb = null!;
        private Mock<IGoogleDriveConnector> _googleDrive = null!;
        private ConnectionSpaceService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _resolver = new Mock<IConnectionResolver>();
            _smb = new Mock<ISmbConnector>();
            _googleDrive = new Mock<IGoogleDriveConnector>();
            _service = new ConnectionSpaceService(_resolver.Object, _smb.Object, _googleDrive.Object, NullLogger<ConnectionSpaceService>.Instance);
        }

        [Test]
        public async Task GetSpaceAsync_Smb_DispatchesToSmbConnector()
        {
            var info = new SmbConnectionInfo("h", 445, "s", null, "u", "p", null);
            var expected = new StorageSpace(1000, 400, false);
            _resolver.Setup(r => r.GetTypeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(ConnectionType.Smb);
            _resolver.Setup(r => r.GetSmbInfoAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(info);
            _smb.Setup(c => c.GetFreeSpaceAsync(info, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            var result = await _service.GetSpaceAsync(1);

            result.Should().Be(expected);
            _googleDrive.Verify(c => c.GetFreeSpaceAsync(It.IsAny<GoogleDriveConnectionInfo>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetSpaceAsync_GoogleDrive_DispatchesToGoogleConnector()
        {
            var info = new GoogleDriveConnectionInfo("cid", "secret", "token", "e@x.com", null);
            var expected = new StorageSpace(null, null, Unlimited: true);
            _resolver.Setup(r => r.GetTypeAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(ConnectionType.GoogleDrive);
            _resolver.Setup(r => r.GetGoogleDriveInfoAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(info);
            _googleDrive.Setup(c => c.GetFreeSpaceAsync(info, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

            var result = await _service.GetSpaceAsync(2);

            result.Should().Be(expected);
            _smb.Verify(c => c.GetFreeSpaceAsync(It.IsAny<SmbConnectionInfo>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetSpaceAsync_ReturnsNull_WhenResolverThrows()
        {
            _resolver.Setup(r => r.GetTypeAsync(3, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

            var result = await _service.GetSpaceAsync(3);

            result.Should().BeNull();
        }
    }
}
