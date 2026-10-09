using BackupService.Components.Controls;
using BackupService.DatabaseBackup;
using BackupService.Enumerations;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.DatabaseBackup
{
    /// <summary>
    /// Settings panel for the built-in application-database backup: enable automatic backups, choose
    /// where they go (local folder or a connection), the schedule and how many to keep — plus a manual
    /// "Back up now" and a Restore action (staged behind a confirmation, applied on the next startup).
    /// </summary>
    public partial class DatabaseBackupPanel : ComponentBase
    {
        [Inject]
        private IDatabaseBackupService DbBackupService { get; set; } = default!;

        // The panel syncs the scheduler after a save (the service can't — it would be a DI cycle).
        [Inject]
        private IDatabaseBackupScheduler Scheduler { get; set; } = default!;

        private bool _loaded;
        private bool _enabled;
        private int? _connectionId;
        private string _folder = string.Empty;
        private ScheduleDefinition? _schedule;
        private int _maxBackups = 5;

        private string? _error;
        private bool _showSchedule;
        private bool _busy;

        private IReadOnlyList<DatabaseBackupInfo> _backups = [];
        private string? _backupsError;
        private DatabaseBackupInfo? _restoreTarget;
        private Notification _notification = default!;

        private string ScheduleText => _schedule is not null ? _schedule.ToHumanReadable() : "Not scheduled";

        private string RestoreMessage => _restoreTarget is null
            ? string.Empty
            : $"Replace the application database with '{_restoreTarget.FileName}' " +
              $"(taken {_restoreTarget.Timestamp:yyyy-MM-dd HH:mm:ss})? All current profiles, connections, " +
              "settings and history will be overwritten. The restore is applied the next time Backup Service starts.";

        protected override async Task OnInitializedAsync()
        {
            var settings = await DbBackupService.GetSettingsAsync();
            _enabled = settings.Enabled;
            _connectionId = settings.TargetConnectionId;
            _folder = settings.TargetFolder;
            _schedule = ScheduleDefinition.FromCron(settings.Schedule);
            _maxBackups = settings.MaxBackups;
            _loaded = true;

            await LoadBackupsAsync();
        }

        private async Task LoadBackupsAsync()
        {
            try
            {
                _backups = await DbBackupService.ListBackupsAsync();
                _backupsError = null;
            }
            catch (Exception ex)
            {
                // An unreachable/unconfigured target isn't fatal — show why the list is empty.
                _backups = [];
                _backupsError = ex.Message;
            }
        }

        private void OpenSchedule() => _showSchedule = true;

        private void OnScheduleApplied(ScheduleDefinition definition)
        {
            _schedule = definition;
            _showSchedule = false;
        }

        private async Task SaveAsync()
        {
            if (_maxBackups < 1)
            {
                _error = "Backups to keep must be at least 1.";
                return;
            }

            if (_connectionId is null && string.IsNullOrWhiteSpace(_folder))
            {
                _error = "Choose a folder to store the backups in.";
                return;
            }

            if (_enabled && _schedule is null)
            {
                _error = "Set a schedule for automatic backups (or disable them).";
                return;
            }

            _error = null;
            await DbBackupService.UpdateSettingsAsync(_enabled, _connectionId, _folder, _schedule?.ToCron(), _maxBackups);
            await Scheduler.SyncAsync();
            _notification.Show("Database backup settings saved", NotificationLevel.Success);

            // The target may have changed — re-list from the new location.
            await LoadBackupsAsync();
        }

        private async Task BackUpNowAsync()
        {
            _busy = true;
            try
            {
                var ran = await DbBackupService.RunBackupAsync(manual: true);
                if (ran)
                {
                    _notification.Show("Database backup created", NotificationLevel.Success);
                }
                else if (DbBackupService.IsRunning)
                {
                    // Skipped, not failed: the scheduled backup (or another tab's) is still going, and Logs has no
                    // failure to show.
                    _notification.Show("A database backup is already running — it will appear here when it finishes.", NotificationLevel.Warning);
                }
                else
                {
                    _notification.Show("Database backup failed — see Logs", NotificationLevel.Error);
                }
                await LoadBackupsAsync();
            }
            finally
            {
                _busy = false;
            }
        }

        private void OpenRestore(DatabaseBackupInfo backup) => _restoreTarget = backup;

        private void CancelRestore() => _restoreTarget = null;

        private async Task ConfirmRestoreAsync()
        {
            var target = _restoreTarget;
            _restoreTarget = null;
            if (target is null)
            {
                return;
            }

            _busy = true;
            try
            {
                await DbBackupService.StageRestoreAsync(target.FileName);
                _notification.Show("Restore staged — restart Backup Service to apply it", NotificationLevel.Warning);
            }
            catch (Exception ex)
            {
                _notification.Show($"Restore failed: {ex.Message}", NotificationLevel.Error);
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
