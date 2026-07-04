using BackupService.Components.Controls;
using BackupService.Connections;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Groups;
using BackupService.Profiles;
using BackupService.Scheduling;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Profiles : ComponentBase, IDisposable
    {
        // No paging — the table lives in a scroll panel, so load every matching row on one "page".
        private const int PageSize = int.MaxValue;
        private const string ArrangeByTypeKey = "profiles.arrangeByType";
        private const string ArrangeByGroupKey = "profiles.arrangeByGroup";

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
        private IConnectionService ConnectionService { get; set; } = default!;

        [Inject]
        private IGroupService GroupService { get; set; } = default!;

        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

        [Inject]
        private IBackupRunner BackupRunner { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Profile? _progressTarget;
        private Notification _notification = default!;

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

        // "Arrange by Group" sections the flat list under group-name headings (mutually exclusive with
        // "Arrange by Type"). _groups supplies the id→name map for the Group column and section headings.
        private bool _arrangeByGroup;
        private IReadOnlyList<GroupSummary> _groups = [];

        private IEnumerable<(ProfileType Type, string Label)> VisibleTabs =>
            TabOrder.Where(t => _typeCounts.GetValueOrDefault(t.Type) > 0);

        private bool HasAnyProfiles => _typeCounts.Values.Sum() > 0;

        // Flat-mode (non-grouped) filters, shown in the filter bar under the heading.
        private string _filterText = string.Empty;
        private ProfileType? _filterType;
        private bool? _filterEnabled;
        private int? _filterConnection;

        // Connection filter options (null = "All connections"), plus the summaries for their labels.
        private IReadOnlyList<ConnectionSummary> _connections = [];
        private IReadOnlyList<int?> _connectionOptions = [null];

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

        private string ConnectionLabel(int? id) =>
            id is { } cid ? _connections.FirstOrDefault(c => c.Id == cid)?.Name ?? $"Connection {cid}" : "All connections";

        private bool HasActiveFilter =>
            !string.IsNullOrWhiteSpace(_filterText) || _filterType is not null || _filterEnabled is not null || _filterConnection is not null;

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

            var savedType = await JS.InvokeAsync<string?>("localStorage.getItem", ArrangeByTypeKey);
            var savedGroup = await JS.InvokeAsync<string?>("localStorage.getItem", ArrangeByGroupKey);
            // The two arrangements are mutually exclusive; if both were somehow stored, group wins.
            _arrangeByGroup = savedGroup == "1";
            _arrangeByType = !_arrangeByGroup && savedType == "1";
            _connections = await ConnectionService.GetSummariesAsync();
            _connectionOptions = new List<int?> { null }.Concat(_connections.Select(c => (int?)c.Id)).ToList();
            _groups = await GroupService.GetSummariesAsync();
            await LoadAsync();
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
                    await LoadAsync();
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
        }

        private async Task LoadAsync()
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
                1, PageSize, _sortColumn, _descending, typeFilter, _filterText, _filterEnabled, _filterConnection);
        }

        private async Task OnFilterTextChanged(ChangeEventArgs e)
        {
            _filterText = e.Value?.ToString() ?? string.Empty;
            await LoadAsync();
        }

        private async Task ClearFilterText()
        {
            _filterText = string.Empty;
            await LoadAsync();
        }

        private async Task OnFilterTypeChanged(ProfileType? type)
        {
            _filterType = type;
            await LoadAsync();
        }

        private async Task OnFilterEnabledChanged(bool? enabled)
        {
            _filterEnabled = enabled;
            await LoadAsync();
        }

        private async Task OnFilterConnectionChanged(int? connectionId)
        {
            _filterConnection = connectionId;
            await LoadAsync();
        }

        private async Task ClearFilters()
        {
            _filterText = string.Empty;
            _filterType = null;
            _filterEnabled = null;
            _filterConnection = null;
            await LoadAsync();
        }

        private async Task ToggleArrangeByTypeAsync(ChangeEventArgs e)
        {
            _arrangeByType = e.Value is true;
            await JS.InvokeVoidAsync("localStorage.setItem", ArrangeByTypeKey, _arrangeByType ? "1" : "0");

            // Mutually exclusive with "Arrange by Group".
            if (_arrangeByType && _arrangeByGroup)
            {
                _arrangeByGroup = false;
                await JS.InvokeVoidAsync("localStorage.setItem", ArrangeByGroupKey, "0");
            }

            await LoadAsync();
        }

        private async Task ToggleArrangeByGroupAsync(ChangeEventArgs e)
        {
            _arrangeByGroup = e.Value is true;
            await JS.InvokeVoidAsync("localStorage.setItem", ArrangeByGroupKey, _arrangeByGroup ? "1" : "0");

            // Mutually exclusive with "Arrange by Type".
            if (_arrangeByGroup && _arrangeByType)
            {
                _arrangeByType = false;
                await JS.InvokeVoidAsync("localStorage.setItem", ArrangeByTypeKey, "0");
            }

            await LoadAsync();
        }

        // The Schedule cell: a USB device-triggered profile (FolderPair/ArchiveSync whose source or target is
        // a USB connection) has no cron — it runs when its device(s) connect — so show the triggering
        // connection(s) instead of "Not scheduled". Reads the connections off the profile entity (included by
        // GetPageAsync) so it's always available, independent of run status. Static: purely entity-derived.
        private static string ScheduleCell(Profile profile)
        {
            // LightroomArchive is watcher-driven (never scheduled); rather than "Not scheduled", list the folder
            // names it monitors — the source folder of each of its actions, names only (not the full paths),
            // comma-separated and capped at 128 characters.
            if (profile.Type == ProfileType.LightroomArchive && profile.LightroomArchiveItems.Count > 0)
            {
                var folders = string.Join(", ", profile.LightroomArchiveItems.Select(i => FolderName(i.SourceFolder)));
                if (folders.Length > MonitorFoldersMaxLength)
                {
                    folders = folders[..(MonitorFoldersMaxLength - 1)].TrimEnd() + "…";
                }

                return $"Monitor: {folders}";
            }

            var usbNames = new List<string>();
            foreach (var connection in new[] { profile.SourceConnection, profile.TargetConnection })
            {
                if (connection is { Type: ConnectionType.Usb } && !usbNames.Contains(connection.Name))
                {
                    usbNames.Add(connection.Name);
                }
            }

            return usbNames.Count > 0
                ? $"On connect: {string.Join(", ", usbNames)}"
                : ScheduleDefinition.Describe(profile.Schedule);
        }

        // Cap for the Lightroom "Monitor: …" folder list; the last character is reserved for a trailing ellipsis.
        private const int MonitorFoldersMaxLength = 128;

        // The trailing folder name of a path (last segment), tolerating trailing separators and drive-only paths.
        private static string FolderName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ' ');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        // The Group column / section label for a profile's group id ("—" when ungrouped or unknown).
        private string GroupName(int? groupId) =>
            groupId is { } id ? _groups.FirstOrDefault(g => g.Id == id)?.Name ?? "—" : "—";

        // Number of visible columns, so an "Arrange by Group" section header can span the full row.
        private int ColSpan =>
            2                                  // Enabled + Name
            + (_arrangeByType ? 0 : 1)          // Type (hidden when arranged by type)
            + (_arrangeByGroup ? 0 : 1)         // Group (hidden when arranged by group)
            + 3                                 // Schedule + Date last run + Status
            + 1;                                // actions

        // The profiles grouped into sections for "Arrange by Group": one section per group (alphabetical by
        // name), then an ungrouped "No Group" section last. Rows keep the page's existing sort order within
        // each section.
        private IEnumerable<(string Name, IReadOnlyList<Profile> Profiles)> GroupSections()
        {
            if (_profiles is null)
            {
                yield break;
            }

            var grouped = _profiles.Items
                .Where(p => p.GroupId is not null)
                .GroupBy(p => p.GroupId!.Value)
                .Select(g => (Name: GroupName(g.Key), Profiles: (IReadOnlyList<Profile>)g.ToList()))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var section in grouped)
            {
                yield return section;
            }

            var ungrouped = _profiles.Items.Where(p => p.GroupId is null).ToList();
            if (ungrouped.Count > 0)
            {
                yield return ("No Group", ungrouped);
            }
        }

        private async Task SelectTypeAsync(string key)
        {
            if (Enum.TryParse<ProfileType>(key, out var type) && type != _activeType)
            {
                _activeType = type;
                await LoadAsync();
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

            await LoadAsync();
        }

        private string SortIndicator(ProfileSortColumn column) =>
            _sortColumn != column ? string.Empty : _descending ? " ▼" : " ▲";

        private bool IsRunning(int id) => StatusService.Get(id) == ProfileStatus.Running;

        private string RunTitle(int id) => IsRunning(id) ? "A backup is already running" : "Run now";

        private string EditTitle(int id) => IsRunning(id) ? "Cannot edit while a backup is running" : "Edit profile";


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
            await LoadAsync();
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

        // Opens the live progress dialog for a running profile (shown alongside the Stop button).
        private void OpenProgress(Profile profile) => _progressTarget = profile;

        private void CloseProgress() => _progressTarget = null;

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

        private async Task DuplicateProfile(Profile profile)
        {
            var newId = await ProfileService.DuplicateAsync(profile.Id);
            if (newId > 0)
            {
                // The duplicate is created disabled — surface that so the admin knows to review and enable it.
                _notification.Show($"Duplicated '{profile.Name}' (created disabled)", NotificationLevel.Success);
                await LoadAsync();
            }
            else
            {
                _notification.Show("Could not duplicate the profile", NotificationLevel.Error);
            }
        }

        // Raised by ProfileDialog after it deletes the profile being edited. The profile was locked on open;
        // DeleteAsync (inside the dialog) already removed its status/lock entries, so just close and refresh.
        private async Task OnDeleted()
        {
            UnlockEditing();
            _showDialog = false;
            _notification.Show("Profile deleted", NotificationLevel.Success);
            await LoadAsync();
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
