using BackupService.Database;
using BackupService.DatabaseBackup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.DatabaseBackup
{
    [TestFixture]
    public class DatabaseBackupSchedulerServiceTests
    {
        private static DatabaseBackupSchedulerService CreateService(DatabaseBackupSettings settings)
        {
            var backupService = new Mock<IDatabaseBackupService>();
            backupService
                .Setup(s => s.GetSettingsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(settings);

            return new DatabaseBackupSchedulerService(backupService.Object, NullLogger<DatabaseBackupSchedulerService>.Instance);
        }

        [Test]
        public async Task Sync_EnabledWithValidCron_Schedules()
        {
            using var service = CreateService(new DatabaseBackupSettings { Enabled = true, Schedule = "0 3 * * *" });

            await service.SyncAsync();

            service.IsScheduled.Should().BeTrue();
        }

        [Test]
        public async Task Sync_Disabled_Unschedules()
        {
            using var service = CreateService(new DatabaseBackupSettings { Enabled = false, Schedule = "0 3 * * *" });

            await service.SyncAsync();

            service.IsScheduled.Should().BeFalse();
        }

        [Test]
        public async Task Sync_EnabledWithoutOrWithInvalidCron_Unschedules()
        {
            using var noCron = CreateService(new DatabaseBackupSettings { Enabled = true, Schedule = null });
            await noCron.SyncAsync();
            noCron.IsScheduled.Should().BeFalse();

            using var badCron = CreateService(new DatabaseBackupSettings { Enabled = true, Schedule = "not a cron" });
            await badCron.SyncAsync();
            badCron.IsScheduled.Should().BeFalse();
        }
    }
}
