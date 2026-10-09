using BackupService.Connections.Usb;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Notifications;
using BackupService.Profiles;
using BackupService.Scheduling;
using BackupService.Scheduling.Usb;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Usb
{
    [TestFixture]
    public class UsbTriggeredRunnerTests
    {
        private const string MountPath = @"E:\";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private ProfileStatusService _status = null!;
        private UsbRunGate _gate = null!;
        private Mock<IBackupRunner> _runner = null!;
        private Mock<IUsbConnector> _connector = null!;
        private Mock<IUsbEjector> _ejector = null!;
        private Mock<IDesktopNotifier> _notifier = null!;
        private int _cardId;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<BackupDbContext>().UseSqlite(_connection).Options;
            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            var factory = new Mock<IDatabaseContextFactory>();
            factory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            _dbFactory = factory.Object;

            _status = new ProfileStatusService();
            _gate = new UsbRunGate();
            _runner = new Mock<IBackupRunner>();
            _runner.Setup(r => r.RunAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _connector = new Mock<IUsbConnector>();
            _connector.Setup(c => c.FindMountPath(It.IsAny<UsbConnectionInfo>())).Returns(MountPath);
            _ejector = new Mock<IUsbEjector>();
            _ejector.Setup(e => e.TryEject(It.IsAny<string>())).Returns(true);
            _notifier = new Mock<IDesktopNotifier>();

            _cardId = SeedConnection("Camera card");
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private UsbTriggeredRunner CreateSut() =>
            new(_dbFactory, _runner.Object, _status, _gate, _connector.Object, _ejector.Object, _notifier.Object,
                NullLogger<UsbTriggeredRunner>.Instance)
            {
                PollInterval = TimeSpan.FromMilliseconds(20),
            };

        private UsbEjectRequest EjectCard(bool notify = true) =>
            new(_cardId, new UsbConnectionInfo(UsbDeviceKind.MassStorage, "HW1", "VOL1", null, null), "Camera card", notify);

        private int SeedConnection(string name)
        {
            using var db = new BackupDbContext(_options);
            var connection = new Connection { Name = name, Type = ConnectionType.Usb, DateCreated = DateTimeOffset.UtcNow };
            db.Connections.Add(connection);
            db.SaveChanges();
            return connection.Id;
        }

        private int SeedProfile(int? sourceConnectionId = null, int? targetConnectionId = null)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = "Profile",
                Type = ProfileType.OneWaySync,
                DateCreated = DateTimeOffset.UtcNow,
                SourceConnectionId = sourceConnectionId,
                TargetConnectionId = targetConnectionId,
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        [Test]
        public async Task RunsTheBatch_ThenEjects_AndSaysItIsSafeToUnplug()
        {
            var profile = SeedProfile(sourceConnectionId: _cardId);

            await CreateSut().RunThenEjectAsync([profile], [EjectCard()], CancellationToken.None);

            _runner.Verify(r => r.RunAsync(profile, false, It.IsAny<CancellationToken>()), Times.Once);
            _ejector.Verify(e => e.TryEject(MountPath), Times.Once);
            _notifier.Verify(n => n.NotifyDeviceEjected("Camera card"), Times.Once);
        }

        [Test]
        public async Task WaitsForAnotherRunOfAProfileUsingTheDevice_BeforeEjecting()
        {
            // A profile already running (or queued behind its group) when the card arrived: the batch's own run of it
            // is refused, but the eject must still wait for it.
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            var alreadyRunning = SeedProfile(targetConnectionId: _cardId);
            _status.TryBeginRun(alreadyRunning).Should().BeTrue();

            var task = CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None);
            await Task.Delay(200);

            task.IsCompleted.Should().BeFalse();
            _ejector.Verify(e => e.TryEject(It.IsAny<string>()), Times.Never);

            _status.EndRun(alreadyRunning, ProfileStatus.Idle);
            await task.WaitAsync(Timeout);
            _ejector.Verify(e => e.TryEject(MountPath), Times.Once);
        }

        [Test]
        public async Task ARunOnAnotherDevice_DoesNotHoldUpTheEject()
        {
            var otherDevice = SeedConnection("Other drive");
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            var elsewhere = SeedProfile(sourceConnectionId: otherDevice);
            _status.TryBeginRun(elsewhere);

            await CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None).WaitAsync(Timeout);

            _ejector.Verify(e => e.TryEject(MountPath), Times.Once);
        }

        [Test]
        public async Task WaitsForARunHoldingTheDevicesGate_BeforeEjecting()
        {
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            var held = await _gate.AcquireAsync([_cardId], CancellationToken.None);

            var task = CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None);
            await Task.Delay(100);
            _ejector.Verify(e => e.TryEject(It.IsAny<string>()), Times.Never);

            await held.DisposeAsync();
            await task.WaitAsync(Timeout);
            _ejector.Verify(e => e.TryEject(MountPath), Times.Once);
        }

        [Test]
        public async Task AFailedRun_StillEjects()
        {
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            _runner.Setup(r => r.RunAsync(triggered, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            await CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None);

            _ejector.Verify(e => e.TryEject(MountPath), Times.Once);
        }

        [Test]
        public async Task ADeviceAlreadyUnplugged_IsNotEjectedOrAnnounced()
        {
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            _connector.Setup(c => c.FindMountPath(It.IsAny<UsbConnectionInfo>())).Returns((string?)null);

            await CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None);

            _ejector.Verify(e => e.TryEject(It.IsAny<string>()), Times.Never);
            _notifier.Verify(n => n.NotifyDeviceEjected(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task AFailedEject_IsNotAnnouncedAsSafeToUnplug()
        {
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            _ejector.Setup(e => e.TryEject(It.IsAny<string>())).Returns(false);

            await CreateSut().RunThenEjectAsync([triggered], [EjectCard()], CancellationToken.None);

            _notifier.Verify(n => n.NotifyDeviceEjected(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task ShuttingDown_StopsWaitingWithoutEjecting()
        {
            var triggered = SeedProfile(sourceConnectionId: _cardId);
            var alreadyRunning = SeedProfile(targetConnectionId: _cardId);
            _status.TryBeginRun(alreadyRunning);
            using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await CreateSut().RunThenEjectAsync([triggered], [EjectCard()], stopping.Token).WaitAsync(Timeout);

            _ejector.Verify(e => e.TryEject(It.IsAny<string>()), Times.Never);
        }
    }
}
