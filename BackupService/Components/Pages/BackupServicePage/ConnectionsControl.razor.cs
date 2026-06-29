using BackupService.Components.Controls;
using BackupService.Connections;
using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class ConnectionsControl : ComponentBase, IDisposable
    {
        private const int PageSize = 10;

        [Inject]
        private IConnectionService ConnectionService { get; set; } = default!;

        [Inject]
        private IConnectionSpaceService SpaceService { get; set; } = default!;

        private bool _showDialog;
        private int? _editId;
        private Connection? _deleteTarget;
        private Notification _notification = default!;

        private int _page = 1;
        private ConnectionSortColumn _sortColumn = ConnectionSortColumn.Name;
        private bool _descending;
        private PagedResult<Connection>? _connections;

        // Distinct profile count per connection id (omitted = 0 = "Unused"), loaded asynchronously after the
        // grid renders so the list shows immediately. _usageLoaded gates the placeholder vs the value.
        private IReadOnlyDictionary<int, int> _profileUsage = new Dictionary<int, int>();
        private bool _usageLoaded;

        // Free-space is loaded asynchronously per row (each is a remote round-trip). _spaceLoaded marks a row
        // as resolved (the value may legitimately be null = "couldn't determine"); _generation discards results
        // from a superseded page/sort; _spaceCts cancels in-flight queries on reload/dispose.
        private readonly Dictionary<int, StorageSpace?> _space = [];
        private readonly HashSet<int> _spaceLoaded = [];
        private int _generation;
        private CancellationTokenSource _spaceCts = new();

        // "Active" (contactable) is checked per row like free-space, and re-checked every 5s. _activeChecked gates
        // the "…" placeholder; _active holds the last Yes/No. Shares the space-load generation/token so a page or
        // sort change discards in-flight checks.
        private readonly Dictionary<int, bool> _active = [];
        private readonly HashSet<int> _activeChecked = [];
        private const int ActivePollMs = 5000;
        private Timer? _activeTimer;

        protected override async Task OnInitializedAsync()
        {
            await LoadAsync(_page);
            // Poll the live "Active" state; the grid re-renders only when a connection's Yes/No actually changes.
            _activeTimer = new Timer(_ => _ = InvokeAsync(() => RunActiveChecks(initial: false)), null, ActivePollMs, ActivePollMs);
        }

        private async Task LoadAsync(int page)
        {
            _connections = await ConnectionService.GetPageAsync(page, PageSize, _sortColumn, _descending);
            _page = _connections.PageNumber;
            StartSpaceLoad();
            RunActiveChecks(initial: true);
            StartUsageLoad();
        }

        // Kicks off a background free-space query for every connection on the current page.
        private void StartSpaceLoad()
        {
            _spaceCts.Cancel();
            _spaceCts.Dispose();
            _spaceCts = new CancellationTokenSource();
            _space.Clear();
            _spaceLoaded.Clear();

            var generation = ++_generation;
            var token = _spaceCts.Token;

            if (_connections is null)
            {
                return;
            }

            foreach (var connection in _connections.Items)
            {
                var id = connection.Id;
                _ = Task.Run(async () =>
                {
                    StorageSpace? space = null;
                    try
                    {
                        space = await SpaceService.GetSpaceAsync(id, token);
                    }
                    catch
                    {
                        // Best-effort — a failed query shows as "—".
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await InvokeAsync(() =>
                    {
                        if (generation != _generation)
                        {
                            return; // a newer page/sort superseded this result
                        }
                        _space[id] = space;
                        _spaceLoaded.Add(id);
                        StateHasChanged();
                    });
                }, token);
            }
        }

        // Checks whether each connection on the current page is contactable, on a background task per row (each is
        // a live round-trip). On the initial load it clears the cache and shows "…" until each result lands; on a
        // periodic re-check it keeps the shown value and re-renders only a row whose Yes/No flipped. Reuses the
        // space-load generation/token so a superseded page/sort is discarded.
        private void RunActiveChecks(bool initial)
        {
            if (_connections is null)
            {
                return;
            }

            if (initial)
            {
                _active.Clear();
                _activeChecked.Clear();
            }

            var generation = _generation;
            var token = _spaceCts.Token;

            foreach (var connection in _connections.Items)
            {
                var id = connection.Id;
                _ = Task.Run(async () =>
                {
                    bool ok;
                    try
                    {
                        ok = await SpaceService.IsContactableAsync(id, token);
                    }
                    catch
                    {
                        ok = false; // best-effort — shows "No"
                    }

                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await InvokeAsync(() =>
                    {
                        if (generation != _generation)
                        {
                            return; // a newer page/sort superseded this result
                        }
                        var changed = !_activeChecked.Contains(id) || _active.GetValueOrDefault(id) != ok;
                        _active[id] = ok;
                        _activeChecked.Add(id);
                        if (changed)
                        {
                            StateHasChanged();
                        }
                    });
                }, token);
            }
        }

        // Loads the per-connection profile-usage counts in the background (one query for all rows) so the grid
        // doesn't wait on it. Reuses the space-load generation/token so a superseded page/sort is discarded.
        private void StartUsageLoad()
        {
            _usageLoaded = false;
            _profileUsage = new Dictionary<int, int>();

            var generation = _generation;
            var token = _spaceCts.Token;

            _ = Task.Run(async () =>
            {
                IReadOnlyDictionary<int, int> usage;
                try
                {
                    usage = await ConnectionService.GetProfileUsageCountsAsync(token);
                }
                catch
                {
                    return; // best-effort — leaves the placeholder
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                await InvokeAsync(() =>
                {
                    if (generation != _generation)
                    {
                        return; // a newer page/sort superseded this result
                    }
                    _profileUsage = usage;
                    _usageLoaded = true;
                    StateHasChanged();
                });
            }, token);
        }

        public void Dispose()
        {
            _activeTimer?.Dispose();
            _spaceCts.Cancel();
            _spaceCts.Dispose();
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

        private int ProfileCount(int connectionId) => _profileUsage.GetValueOrDefault(connectionId);

        private static string Endpoint(Connection connection) =>
            connection.Smb is { } smb ? $@"\\{smb.Host}\{smb.ShareName}"
            : connection.GoogleDrive is { } gd ? (string.IsNullOrWhiteSpace(gd.AccountEmail) ? "Google Drive" : gd.AccountEmail)
            : "—";

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
