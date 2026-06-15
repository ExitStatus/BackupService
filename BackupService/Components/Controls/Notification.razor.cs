using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Reusable, non-modal toast notification. Call <see cref="Show"/> (via @ref) to
    /// display a message at the bottom-right of the window; it stays visible for
    /// <see cref="VisibleDuration"/> then fades out. Colour reflects the level.
    /// </summary>
    public partial class Notification : ComponentBase, IDisposable
    {
        private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

        private string? _message;
        private NotificationLevel _level;
        private bool _visible;
        private bool _fadingOut;
        private CancellationTokenSource? _cts;

        private string LevelClass => _level switch
        {
            NotificationLevel.Warning => "warning",
            NotificationLevel.Error => "error",
            _ => "success",
        };

        public void Show(string message, NotificationLevel level = NotificationLevel.Success)
        {
            // Cancel any in-progress display so a new message restarts the timer.
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            _message = message;
            _level = level;
            _fadingOut = false;
            _visible = true;
            StateHasChanged();

            _ = RunLifecycleAsync(_cts.Token);
        }

        private async Task RunLifecycleAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(VisibleDuration, token);

                _fadingOut = true;
                await InvokeAsync(StateHasChanged);

                await Task.Delay(FadeDuration, token);

                _visible = false;
                await InvokeAsync(StateHasChanged);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer Show() or disposed — nothing to do.
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}
