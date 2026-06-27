using BackupService.Components.Controls;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.ScheduledTasks;
using BackupService.Scheduling.ScheduledTasks;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    /// <summary>
    /// The Scheduled Tasks section: a sortable, paged grid of scheduled tasks with inline enable toggle,
    /// live status, and Run-now / Stop / edit / delete actions. Mirrors <see cref="Profiles"/> /
    /// <c>ConnectionsControl</c>.
    /// </summary>
    public partial class ScheduledTasksControl : ComponentBase, IDisposable
    {
        private const int PageSize = 10;

        [Inject]
        private IScheduledTaskService TaskService { get; set; } = default!;

        [Inject]
        private IScheduledTaskStatusService StatusService { get; set; } = default!;

        [Inject]
        private IScheduledTaskRunner TaskRunner { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private ScheduledTask? _deleteTarget;
        private Notification _notification = default!;

        private int _page = 1;
        private ScheduledTaskSortColumn _sortColumn = ScheduledTaskSortColumn.Name;
        private bool _descending;
        private PagedResult<ScheduledTask>? _tasks;

        protected override void OnInitialized()
        {
            StatusService.Changed += OnStatusChanged;
        }

        protected override Task OnInitializedAsync() => LoadAsync(_page);

        private void OnStatusChanged(int taskId)
        {
            // When a task on the current page changes status, reload so DateLastRun (written by the runner)
            // and the live Status cell both refresh.
            if (_tasks?.Items.Any(t => t.Id == taskId) == true)
            {
                InvokeAsync(async () =>
                {
                    await LoadAsync(_page);
                    StateHasChanged();
                });
            }
        }

        public void Dispose()
        {
            StatusService.Changed -= OnStatusChanged;

            UnlockEditing();
            if (_deleteTarget is not null)
            {
                StatusService.Unlock(_deleteTarget.Id);
            }
        }

        private async Task LoadAsync(int page)
        {
            _tasks = await TaskService.GetPageAsync(page, PageSize, _sortColumn, _descending);
            _page = _tasks.PageNumber;
        }

        private async Task SortByAsync(ScheduledTaskSortColumn column)
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

        private string SortIndicator(ScheduledTaskSortColumn column) =>
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
            if (_tasks is not null && _page < _tasks.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private bool IsRunning(int id) => StatusService.Get(id) == ProfileStatus.Running;

        private void OpenCreate()
        {
            _editId = null;
            _showDialog = true;
        }

        private void OpenEdit(int id)
        {
            // Lock the task so a scheduled run won't fire while it's being edited.
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
            var message = _editId is null ? "Scheduled task created" : "Scheduled task updated";
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

        private async Task ToggleEnabledAsync(ScheduledTask task, bool enabled)
        {
            await TaskService.SetEnabledAsync(task.Id, enabled);
            task.Enabled = enabled; // update the in-list entity without a full reload
            _notification.Show(enabled ? "Scheduled task enabled" : "Scheduled task disabled", NotificationLevel.Success);
        }

        private void StopRun(ScheduledTask task)
        {
            TaskRunner.RequestStop(task.Id);
            _notification.Show($"Stopping '{task.Name}'…", NotificationLevel.Warning);
        }

        private void RunNow(ScheduledTask task)
        {
            // Run on a background task so a long task doesn't block the UI; the status-change events refresh
            // the grid. RunAsync records its own failures and doesn't throw, so fire-and-forget is safe.
            _ = Task.Run(() => TaskRunner.RunAsync(task.Id, manual: true));
            _notification.Show($"Running '{task.Name}' now", NotificationLevel.Success);
        }

        private void OpenDelete(ScheduledTask task)
        {
            // Lock the task so a scheduled run won't fire while the delete dialog is open.
            StatusService.Lock(task.Id);
            _deleteTarget = task;
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

            await TaskService.DeleteAsync(_deleteTarget.Id);
            _deleteTarget = null;
            _notification.Show("Scheduled task deleted", NotificationLevel.Success);

            if (_tasks is not null && _tasks.Items.Count == 1 && _page > 1)
            {
                _page--;
            }

            await LoadAsync(_page);
        }

        private string StatusText(int taskId) => StatusService.Get(taskId) switch
        {
            ProfileStatus.Idle => "Idle",
            ProfileStatus.Running => "Running",
            ProfileStatus.Error => "Error",
            var s => s.ToString(),
        };
    }
}
