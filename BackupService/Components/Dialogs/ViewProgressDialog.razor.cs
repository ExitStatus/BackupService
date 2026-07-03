using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// A large modal showing the live progress of a running backup profile: an overall progress bar, the
    /// current detail item's progress bar, the file currently being copied, and the run's log terminal
    /// (with the full line filters). Reads live progress from <see cref="IProfileStatusService"/> and finds
    /// the run's operation log (the newest for the profile). Opened from the Profiles grid's run row.
    /// </summary>
    public partial class ViewProgressDialog : ComponentBase, IDisposable
    {
        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        /// <summary>The profile whose progress is shown.</summary>
        [Parameter]
        public int ProfileId { get; set; }

        /// <summary>The profile's display name (shown in the title).</summary>
        [Parameter]
        public string ProfileName { get; set; } = string.Empty;

        [Parameter]
        public EventCallback OnClose { get; set; }

        // The run's operation log id (the newest log for this profile — the one the active run populates).
        private int? _logId;
        private bool _subscribed;

        // The status service clears a profile's progress when its run ends (leaves Running), so cache the
        // last-known snapshot to keep the bars at their final state (~100%) after the run completes.
        private ProfileProgress? _last;

        private ProfileProgress? Progress => StatusService.GetProgressDetail(ProfileId) ?? _last;

        private bool IsRunning => StatusService.Get(ProfileId) == ProfileStatus.Running;

        private string StatusLabel => StatusService.Get(ProfileId) switch
        {
            ProfileStatus.Running => "Running",
            ProfileStatus.Error => "Error",
            _ => "Finished",
        };

        private int OverallPercent => Progress?.TotalPercent ?? 0;

        private int ItemPercent => Progress?.StepPercent ?? 0;

        private string ItemLabel => Progress?.StepName is { Length: > 0 } name ? name : "Current item";

        private string? CurrentFile => Progress?.CurrentFile;

        protected override async Task OnInitializedAsync()
        {
            _last = StatusService.GetProgressDetail(ProfileId);

            // The run creates its operation log at start, so the newest log for this profile is the run's.
            var page = await OperationLogService.GetPageAsync(1, 1, profileId: ProfileId);
            _logId = page.Items.Count > 0 ? page.Items[0].Id : null;
        }

        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                StatusService.ProgressChanged += OnProgressChanged;
                StatusService.Changed += OnStatusChanged;
                _subscribed = true;
            }
        }

        private void OnProgressChanged(int profileId)
        {
            if (profileId == ProfileId)
            {
                if (StatusService.GetProgressDetail(ProfileId) is { } p)
                {
                    _last = p; // remember the latest snapshot for after the run ends
                }
                _ = InvokeAsync(StateHasChanged);
            }
        }

        private void OnStatusChanged(int profileId)
        {
            if (profileId == ProfileId)
            {
                _ = InvokeAsync(StateHasChanged);
            }
        }

        public void Dispose()
        {
            if (_subscribed)
            {
                StatusService.ProgressChanged -= OnProgressChanged;
                StatusService.Changed -= OnStatusChanged;
            }

            // Drop the cached snapshot; the terminal's line buffers are freed by the child
            // LogTerminal's own Dispose.
            _last = null;
        }
    }
}
