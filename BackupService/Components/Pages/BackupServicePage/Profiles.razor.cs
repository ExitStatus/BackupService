using BackupService.Components.Controls;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Profiles;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Profiles : ComponentBase, IDisposable
    {
        private const int PageSize = 10;
        private const string ArrangeByTypeKey = "profiles.arrangeByType";

        // The "Arrange by Type" tabs, in display order, with their literal labels (suffixed with a count).
        private static readonly (ProfileType Type, string Label)[] TabOrder =
        [
            (ProfileType.FolderPair, "Folder Pair Sync"),
            (ProfileType.ArchiveSync, "Archive Sync"),
            (ProfileType.InstantSync, "Instant Sync"),
            (ProfileType.LightroomArchive, "Lightroom Sync"),
        ];

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

        [Inject]
        private IBackupRunner BackupRunner { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Profile? _deleteTarget;
        private Notification _notification = default!;

        private int _page = 1;
        private ProfileSortColumn _sortColumn = ProfileSortColumn.Name;
        private bool _descending;
        private PagedResult<Profile>? _profiles;

        // "Arrange by Type" grouped view. _initialised gates the body until the first interactive render has
        // read the persisted preference (localStorage is unavailable during prerender), avoiding a flash of the
        // flat list when the saved view is grouped.
        private bool _arrangeByType;
        private bool _initialised;
        private ProfileType _activeType = ProfileType.FolderPair;
        private IReadOnlyDictionary<ProfileType, int> _typeCounts = new Dictionary<ProfileType, int>();

        private IEnumerable<(ProfileType Type, string Label)> VisibleTabs =>
            TabOrder.Where(t => _typeCounts.GetValueOrDefault(t.Type) > 0);

        private bool HasAnyProfiles => _typeCounts.Values.Sum() > 0;

        // Flat-mode (non-grouped) filters, shown in the filter bar under the heading.
        private string _filterText = string.Empty;
        private ProfileType? _filterType;
        private bool? _filterEnabled;

        // Dropdown options: null is the "All …" choice.
        private static readonly ProfileType?[] TypeOptions =
            [null, .. Enum.GetValues<ProfileType>().Select(t => (ProfileType?)t)];
        private static readonly bool?[] EnabledOptions = [null, true, false];

        private static string TypeLabel(ProfileType? type) => type is { } t ? t.GetDescription() : "All types";
        private static string EnabledLabel(bool? enabled) => enabled switch
        {
            true => "Enabled",
            false => "Disabled",
            null => "All states",
        };

        private bool HasActiveFilter =>
            !string.IsNullOrWhiteSpace(_filterText) || _filterType is not null || _filterEnabled is not null;

        protected override void OnInitialized()
        {
            StatusService.Changed += OnStatusChanged;
            StatusService.ProgressChanged += OnProgressChanged;
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender)
            {
                return;
            }

            var saved = await JS.InvokeAsync<string?>("localStorage.getItem", ArrangeByTypeKey);
            _arrangeByType = saved == "1";
            await LoadAsync(1);
            _initialised = true;
            StateHasChanged();
        }

        private void OnStatusChanged(int profileId)
        {
            // When a profile on the current page changes status, reload the page data so fields the
            // run updates (e.g. DateLastRun) refresh too — the Status cell reads the live service,
            // but DateLastRun comes from the loaded entity, which is otherwise stale.
            if (_profiles?.Items.Any(p => p.Id == profileId) == true)
            {
                InvokeAsync(async () =>
                {
                    await LoadAsync(_page);
                    StateHasChanged();
                });
            }
        }

        private void OnProgressChanged(int profileId)
        {
            // A percent tick updates just the Status cell (which reads the live service) — no DB reload,
            // so the grid doesn't flicker or lose its sort/scroll position.
            if (_profiles?.Items.Any(p => p.Id == profileId) == true)
            {
                InvokeAsync(StateHasChanged);
            }
        }

        public void Dispose()
        {
            StatusService.Changed -= OnStatusChanged;
            StatusService.ProgressChanged -= OnProgressChanged;

            // Release any lock held by an open dialog so it can't leak if we're torn down.
            UnlockEditing();
            if (_deleteTarget is not null)
            {
                StatusService.Unlock(_deleteTarget.Id);
            }
        }

        private async Task LoadAsync(int page)
        {
            _typeCounts = await ProfileService.GetCountsByTypeAsync();

            // Keep the active tab valid as profiles are created/deleted: fall back to the first visible tab.
            if (_arrangeByType && _typeCounts.GetValueOrDefault(_activeType) == 0)
            {
                _activeType = VisibleTabs.Select(t => t.Type).FirstOrDefault();
            }

            // The text/enabled filters apply in both modes; the type filter comes from the active tab when
            // grouped, or the type dropdown when flat.
            var typeFilter = _arrangeByType ? (HasAnyProfiles ? (ProfileType?)_activeType : null) : _filterType;
            _profiles = await ProfileService.GetPageAsync(
                page, PageSize, _sortColumn, _descending, typeFilter, _filterText, _filterEnabled);
            _page = _profiles.PageNumber;
        }

        private async Task OnFilterTextChanged(ChangeEventArgs e)
        {
            _filterText = e.Value?.ToString() ?? string.Empty;
            await LoadAsync(1);
        }

        private async Task ClearFilterText()
        {
            _filterText = string.Empty;
            await LoadAsync(1);
        }

        private async Task OnFilterTypeChanged(ProfileType? type)
        {
            _filterType = type;
            await LoadAsync(1);
        }

        private async Task OnFilterEnabledChanged(bool? enabled)
        {
            _filterEnabled = enabled;
            await LoadAsync(1);
        }

        private async Task ClearFilters()
        {
            _filterText = string.Empty;
            _filterType = null;
            _filterEnabled = null;
            await LoadAsync(1);
        }

        private async Task ToggleArrangeByTypeAsync(ChangeEventArgs e)
        {
            _arrangeByType = e.Value is true;
            await JS.InvokeVoidAsync("localStorage.setItem", ArrangeByTypeKey, _arrangeByType ? "1" : "0");
            await LoadAsync(1);
        }

        private async Task SelectTypeAsync(string key)
        {
            if (Enum.TryParse<ProfileType>(key, out var type) && type != _activeType)
            {
                _activeType = type;
                await LoadAsync(1);
            }
        }

        // The visible tabs as TabBar items: each label suffixed with that type's profile count, e.g. "(3)".
        private IReadOnlyList<TabBar.TabItem> TabItems() =>
            VisibleTabs
                .Select(t => new TabBar.TabItem(t.Type.ToString(), $"{t.Label} ({_typeCounts.GetValueOrDefault(t.Type)})"))
                .ToList();

        private async Task SortByAsync(ProfileSortColumn column)
        {
            if (_sortColumn == column)
            {
                _descending = !_descending;
            }
            else
            {
                _sortColumn = column;
                _descending = false;
            }

            await LoadAsync(1);
        }

        private string SortIndicator(ProfileSortColumn column) =>
            _sortColumn != column ? string.Empty : _descending ? " ▼" : " ▲";

        private async Task PreviousPageAsync()
        {
            if (_page > 1)
            {
                await LoadAsync(_page - 1);
            }
        }

        private async Task NextPageAsync()
        {
            if (_profiles is not null && _page < _profiles.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private bool IsRunning(int id) => StatusService.Get(id) == ProfileStatus.Running;

        private string RunTitle(int id) => IsRunning(id) ? "A backup is already running" : "Run now";

        private string EditTitle(int id) => IsRunning(id) ? "Cannot edit while a backup is running" : "Edit profile";

        private string DeleteTitle(int id) => IsRunning(id) ? "Cannot delete while a backup is running" : "Delete profile";

        private void OpenCreate()
        {
            _editId = null;
            _showDialog = true;
        }

        private void OpenEdit(int id)
        {
            // Lock the profile so a scheduled run won't fire while it's being edited.
            StatusService.Lock(id);
            _editId = id;
            _showDialog = true;
        }

        private void CancelDialog()
        {
            UnlockEditing();
            _showDialog = false;
        }

        private async Task OnSaved()
        {
            UnlockEditing();
            var message = _editId is null ? "Profile created" : "Profile updated";
            _showDialog = false;
            _notification.Show(message, NotificationLevel.Success);
            await LoadAsync(_page);
        }

        private void UnlockEditing()
        {
            if (_editId is int id)
            {
                StatusService.Unlock(id);
            }
        }

        private async Task ToggleEnabledAsync(Profile profile, bool enabled)
        {
            await ProfileService.SetEnabledAsync(profile.Id, enabled);
            profile.Enabled = enabled; // update the in-list entity without a full reload
            _notification.Show(enabled ? "Profile enabled" : "Profile disabled", NotificationLevel.Success);
        }

        // The Stop button replaces the disabled Run/Edit/Delete buttons while a scheduled backup
        // (folder pair or archive) is running, so the user can safely cancel an in-progress run.
        private bool ShowStop(Profile profile) =>
            IsRunning(profile.Id) && profile.Type is ProfileType.FolderPair or ProfileType.ArchiveSync;

        private void StopRun(Profile profile)
        {
            // Cooperative cancel: the run unwinds cleanly (no temp files), is logged as a warning, and
            // the profile returns to Idle to wait for its next scheduled run.
            BackupRunner.RequestStop(profile.Id);
            _notification.Show($"Stopping '{profile.Name}'…", NotificationLevel.Warning);
        }

        private void RunNow(Profile profile)
        {
            // Run on a background task (like the scheduler) so a long backup doesn't block the UI;
            // the status-change events refresh the grid as it progresses. RunAsync records its own
            // failures (Error status + operation log) and doesn't throw, so fire-and-forget is safe.
            _ = Task.Run(() => BackupRunner.RunAsync(profile.Id, manual: true));
            _notification.Show($"Running '{profile.Name}' now", NotificationLevel.Success);
        }

        private void OpenDelete(Profile profile)
        {
            // Lock the profile so a scheduled run won't fire while the delete dialog is open.
            StatusService.Lock(profile.Id);
            _deleteTarget = profile;
        }

        private void CancelDelete()
        {
            if (_deleteTarget is not null)
            {
                StatusService.Unlock(_deleteTarget.Id);
            }
            _deleteTarget = null;
        }

        private async Task ConfirmDeleteAsync()
        {
            if (_deleteTarget is null)
            {
                return;
            }

            // DeleteAsync removes the profile's status and lock entries.
            await ProfileService.DeleteAsync(_deleteTarget.Id);
            _deleteTarget = null;
            _notification.Show("Profile deleted", NotificationLevel.Success);

            // Step back a page if we just removed the last row on it.
            if (_profiles is not null && _profiles.Items.Count == 1 && _page > 1)
            {
                _page--;
            }

            await LoadAsync(_page);
        }

        // The live Status cell text: a running profile shows its progress percent when known.
        private string StatusText(int profileId)
        {
            var status = StatusService.Get(profileId);
            if (status == ProfileStatus.Running)
            {
                return StatusService.GetProgress(profileId) is { } percent ? $"Running - {percent}%" : "Running";
            }
            return DescribeStatus(status);
        }

        private static string DescribeStatus(ProfileStatus status) => status switch
        {
            ProfileStatus.Idle => "Idle",
            ProfileStatus.Running => "Running",
            ProfileStatus.Error => "Error",
            _ => status.ToString(),
        };
    }
}
