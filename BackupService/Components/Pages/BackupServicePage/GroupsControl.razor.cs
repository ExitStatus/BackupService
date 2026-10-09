using BackupService.Components.Controls;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Groups;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class GroupsControl : ComponentBase, IDisposable
    {
        // No paging — the table lives in a scroll panel, so load every row on one "page".
        private const int PageSize = int.MaxValue;

        [Inject]
        private IGroupService GroupService { get; set; } = default!;

        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Group? _deleteTarget;
        private Notification _notification = default!;

        private GroupSortColumn _sortColumn = GroupSortColumn.Name;
        private bool _descending;
        private PagedResult<Group>? _groups;

        // Per-group member stats (count, last-run-started, member ids), loaded alongside the page. The live
        // running count is derived from the member ids via the status service and refreshed on Changed.
        private IReadOnlyDictionary<int, GroupProfileStats> _stats = new Dictionary<int, GroupProfileStats>();

        protected override async Task OnInitializedAsync()
        {
            StatusService.Changed += OnStatusChanged;
            await LoadAsync();
        }

        private void OnStatusChanged(int profileId)
        {
            // A member's run started/finished — re-render so the live "Running" count updates. No DB reload
            // is needed for the tick (the count is computed from the status service), but a completed run
            // updates DateLastRun, so reload the stats to refresh "Last run" too.
            InvokeAsync(async () =>
            {
                _stats = await GroupService.GetProfileStatsAsync();
                StateHasChanged();
            });
        }

        public void Dispose()
        {
            StatusService.Changed -= OnStatusChanged;
        }

        private async Task LoadAsync()
        {
            _groups = await GroupService.GetPageAsync(1, PageSize, _sortColumn, _descending);
            _stats = await GroupService.GetProfileStatsAsync();
        }

        private async Task SortByAsync(GroupSortColumn column)
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

        private string SortIndicator(GroupSortColumn column) =>
            _sortColumn != column ? string.Empty : _descending ? " ▼" : " ▲";

        private int ProfileCount(int groupId) => _stats.GetValueOrDefault(groupId)?.Count ?? 0;

        private int RunningCount(int groupId) =>
            _stats.GetValueOrDefault(groupId) is { } stats
                ? stats.ProfileIds.Count(StatusService.IsRunning)
                : 0;

        private string LastRun(int groupId) =>
            _stats.GetValueOrDefault(groupId)?.LastRun is { } when
                ? when.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : "Never";

        private void OpenCreate()
        {
            _editId = null;
            _showDialog = true;
        }

        private void OpenEdit(int id)
        {
            _editId = id;
            _showDialog = true;
        }

        private void CancelDialog() => _showDialog = false;

        private async Task OnSaved()
        {
            var message = _editId is null ? "Group created" : "Group updated";
            _showDialog = false;
            _notification.Show(message, NotificationLevel.Success);
            await LoadAsync();
        }

        private void OpenDelete(Group group) => _deleteTarget = group;

        private void CancelDelete() => _deleteTarget = null;

        private string DeleteMessage
        {
            get
            {
                if (_deleteTarget is null)
                {
                    return string.Empty;
                }
                var count = ProfileCount(_deleteTarget.Id);
                var suffix = count == 0
                    ? "It has no profiles."
                    : $"Its {count} profile{(count == 1 ? "" : "s")} will be ungrouped (not deleted).";
                return $"Delete the group \"{_deleteTarget.Name}\"? {suffix}";
            }
        }

        private async Task ConfirmDeleteAsync()
        {
            if (_deleteTarget is null)
            {
                return;
            }

            var ungrouped = await GroupService.DeleteAsync(_deleteTarget.Id);
            _deleteTarget = null;

            var message = ungrouped == 0
                ? "Group deleted"
                : $"Group deleted — {ungrouped} profile{(ungrouped == 1 ? "" : "s")} ungrouped";
            _notification.Show(message, NotificationLevel.Success);
            await LoadAsync();
        }
    }
}
