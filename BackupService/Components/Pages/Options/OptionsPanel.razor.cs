using BackupService.Components.Controls;
using BackupService.Enumerations;
using BackupService.Hosting;
using BackupService.Options;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.Options
{
    /// <summary>
    /// Settings panel for the desktop-integration options: start with Windows, show the system tray icon, and
    /// allow completion notifications. Saving persists the flags and applies the autostart registry entry; the
    /// tray service reacts to the change via <see cref="IAppOptionsService.Changed"/>.
    /// </summary>
    public partial class OptionsPanel : ComponentBase
    {
        [Inject]
        private IAppOptionsService OptionsService { get; set; } = default!;

        [Inject]
        private IStartupManager StartupManager { get; set; } = default!;

        private InputModel? _model;
        private Notification _notification = default!;

        protected override async Task OnInitializedAsync()
        {
            var settings = await OptionsService.GetSettingsAsync();
            _model = new InputModel
            {
                StartWithWindows = settings.StartWithWindows,
                ShowTrayIcon = settings.ShowTrayIcon,
                AllowNotifications = settings.AllowNotifications,
            };
        }

        private async Task SaveAsync()
        {
            if (_model is null)
            {
                return;
            }

            await OptionsService.UpdateSettingsAsync(_model.StartWithWindows, _model.ShowTrayIcon, _model.AllowNotifications);
            StartupManager.Apply(_model.StartWithWindows);
            _notification.Show("Options saved", NotificationLevel.Success);
        }

        private sealed class InputModel
        {
            public bool StartWithWindows { get; set; }

            public bool ShowTrayIcon { get; set; }

            public bool AllowNotifications { get; set; }
        }
    }
}
