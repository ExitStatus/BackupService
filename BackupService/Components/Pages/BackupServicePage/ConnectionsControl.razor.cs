using BackupService.Components.Controls;
using BackupService.Connections;
using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class ConnectionsControl : ComponentBase
    {
        private const int PageSize = 10;

        [Inject]
        private IConnectionService ConnectionService { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Connection? _deleteTarget;
        private Notification _notification = default!;

        private int _page = 1;
        private ConnectionSortColumn _sortColumn = ConnectionSortColumn.Name;
        private bool _descending;
        private PagedResult<Connection>? _connections;

        protected override Task OnInitializedAsync() => LoadAsync(_page);

        private async Task LoadAsync(int page)
        {
            _connections = await ConnectionService.GetPageAsync(page, PageSize, _sortColumn, _descending);
            _page = _connections.PageNumber;
        }

        private async Task SortByAsync(ConnectionSortColumn column)
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

        private string SortIndicator(ConnectionSortColumn column) =>
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
            if (_connections is not null && _page < _connections.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private static string Endpoint(Connection connection) =>
            connection.Smb is { } smb ? $@"\\{smb.Host}\{smb.ShareName}" : "—";

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
            var message = _editId is null ? "Connection created" : "Connection updated";
            _showDialog = false;
            _notification.Show(message, NotificationLevel.Success);
            await LoadAsync(_page);
        }

        private void OpenDelete(Connection connection) => _deleteTarget = connection;

        private void CancelDelete() => _deleteTarget = null;

        private async Task ConfirmDeleteAsync()
        {
            if (_deleteTarget is null)
            {
                return;
            }

            var result = await ConnectionService.DeleteAsync(_deleteTarget.Id);
            _deleteTarget = null;

            if (!result.Deleted)
            {
                _notification.Show(result.Error ?? "Connection could not be deleted.", NotificationLevel.Error);
                return;
            }

            _notification.Show("Connection deleted", NotificationLevel.Success);

            // Step back a page if we just removed the last row on it.
            if (_connections is not null && _connections.Items.Count == 1 && _page > 1)
            {
                _page--;
            }

            await LoadAsync(_page);
        }
    }
}
