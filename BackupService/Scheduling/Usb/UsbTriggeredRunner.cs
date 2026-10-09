using BackupService.Connections.Usb;
using BackupService.Database;
using BackupService.Notifications;
using BackupService.Profiles;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// What a USB device arriving sets off, once <see cref="UsbDeviceWatcherService"/> has decided which profiles can
    /// run: runs them, then safely removes any mass-storage device one of them asked to eject. The eject waits for
    /// every run of every profile that uses the device — not just the ones this arrival started: a profile already
    /// running, or queued behind its group, when the device arrived would otherwise have the drive pulled from under
    /// it — and then takes the device's run gate, so a run that slipped in meanwhile finishes first.
    /// </summary>
    public sealed class UsbTriggeredRunner(
        IDatabaseContextFactory contextFactory,
        IBackupRunner backupRunner,
        IProfileStatusService profileStatus,
        IUsbRunGate usbRunGate,
        IUsbConnector usbConnector,
        IUsbEjector usbEjector,
        IDesktopNotifier notifier,
        ILogger<UsbTriggeredRunner> logger)
    {
        /// <summary>How often the eject re-checks whether the device's profiles have finished. A test seam.</summary>
        internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

        public async Task RunThenEjectAsync(
            IReadOnlyCollection<int> profileIds, IReadOnlyCollection<UsbEjectRequest> ejects, CancellationToken cancellationToken)
        {
            try
            {
                await Task.WhenAll(profileIds.Select(id => backupRunner.RunAsync(id, manual: false)));
            }
            catch (Exception ex)
            {
                // The runner records its own failures; this is a last resort so the eject below still happens.
                logger.LogError(ex, "A USB-triggered backup failed.");
            }

            foreach (var eject in ejects)
            {
                try
                {
                    await EjectWhenIdleAsync(eject, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return; // shutting down
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to eject USB device '{Device}'.", eject.Label);
                }
            }
        }

        private async Task EjectWhenIdleAsync(UsbEjectRequest eject, CancellationToken cancellationToken)
        {
            await WaitForProfilesUsingAsync(eject.ConnectionId, cancellationToken);
            await using var gate = await usbRunGate.AcquireAsync([eject.ConnectionId], cancellationToken);

            if (usbConnector.FindMountPath(eject.Device) is not { } mountPath)
            {
                return; // already unplugged
            }

            if (usbEjector.TryEject(mountPath))
            {
                logger.LogInformation("Ejected USB device '{Device}' ({Drive}).", eject.Label, mountPath);
                if (eject.Notify)
                {
                    notifier.NotifyDeviceEjected(string.IsNullOrEmpty(eject.Label) ? mountPath : eject.Label);
                }
            }
            else
            {
                logger.LogWarning("Could not eject USB device '{Device}' ({Drive}) — it may be in use.", eject.Label, mountPath);
            }
        }

        private async Task WaitForProfilesUsingAsync(int connectionId, CancellationToken cancellationToken)
        {
            List<int> users;
            await using (var db = contextFactory.CreateDbContext())
            {
                users = await db.Profiles
                    .AsNoTracking()
                    .Where(p => p.SourceConnectionId == connectionId || p.TargetConnectionId == connectionId)
                    .Select(p => p.Id)
                    .ToListAsync(cancellationToken);
            }

            while (users.Any(profileStatus.IsRunning))
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    /// <summary>A mass-storage USB device to eject once the runs using it are done.</summary>
    /// <param name="ConnectionId">The USB connection bound to the device.</param>
    /// <param name="Device">Its identity, to find where it's mounted now.</param>
    /// <param name="Label">For logs and the notification.</param>
    /// <param name="Notify">Whether to tell the user it's safe to unplug.</param>
    public sealed record UsbEjectRequest(int ConnectionId, UsbConnectionInfo Device, string? Label, bool Notify);
}
