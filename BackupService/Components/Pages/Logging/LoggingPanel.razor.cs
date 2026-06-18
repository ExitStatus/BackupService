using BackupService.Components.Controls;
using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Logging
{
    /// <summary>
    /// Settings panel for the log-retention policy: lets the admin choose how many days of
    /// authentication and operation history to keep before the automatic purge removes older rows.
    /// </summary>
    public partial class LoggingPanel : ComponentBase
    {
        [Inject]
        private ILogRetentionService RetentionService { get; set; } = default!;

        private InputModel? _model;
        private string? _error;
        private Notification _notification = default!;

        protected override async Task OnInitializedAsync()
        {
            var settings = await RetentionService.GetSettingsAsync();
            _model = new InputModel
            {
                AuthenticationLogRetentionDays = settings.AuthenticationLogRetentionDays,
                OperationLogRetentionDays = settings.OperationLogRetentionDays,
            };
        }

        private async Task SaveAsync()
        {
            if (_model is null)
            {
                return;
            }

            if (_model.AuthenticationLogRetentionDays < 1 || _model.OperationLogRetentionDays < 1)
            {
                _error = "Retention must be at least 1 day.";
                return;
            }

            _error = null;
            await RetentionService.UpdateSettingsAsync(_model.AuthenticationLogRetentionDays, _model.OperationLogRetentionDays);
            _notification.Show("Logging settings saved", NotificationLevel.Success);
        }

        private sealed class InputModel
        {
            public int AuthenticationLogRetentionDays { get; set; }

            public int OperationLogRetentionDays { get; set; }
        }
    }
}
