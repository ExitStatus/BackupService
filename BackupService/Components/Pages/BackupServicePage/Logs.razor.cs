using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Logs : ComponentBase
    {
        private const int PageSize = 10;

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        private int _page = 1;
        private PagedResult<OperationLog>? _logs;

        private string _filter = string.Empty;
        private bool _includeMessages;
        private OperationLogLevel? _level;

        // Level filter options: null = "All levels", then each level.
        private static readonly OperationLogLevel?[] LevelOptions =
            [null, .. Enum.GetValues<OperationLogLevel>().Select(l => (OperationLogLevel?)l)];

        private static string LevelLabel(OperationLogLevel? level) =>
            level.HasValue ? level.Value.GetDescription() : "All levels";

        private bool _hasActiveFilter => !string.IsNullOrWhiteSpace(_filter) || _level is not null;

        // Which logs are expanded, and a cache of their detail lines (lazy-loaded on first expand).
        private readonly HashSet<int> _expanded = [];
        private readonly Dictionary<int, IReadOnlyList<OperationLogDetail>> _details = [];

        protected override Task OnInitializedAsync() => LoadAsync(_page);

        private async Task LoadAsync(int page)
        {
            _logs = await OperationLogService.GetPageAsync(page, PageSize, _filter, _includeMessages, _level);
            _page = _logs.PageNumber;

            // Collapse everything when the page changes — ids on this page differ.
            _expanded.Clear();
        }

        private async Task OnFilterChanged(ChangeEventArgs e)
        {
            _filter = e.Value?.ToString() ?? string.Empty;
            await LoadAsync(1);
        }

        private async Task OnIncludeMessagesChanged(ChangeEventArgs e)
        {
            _includeMessages = e.Value is true;

            // Only re-query if a filter is active — the checkbox has no effect without one.
            if (!string.IsNullOrWhiteSpace(_filter))
            {
                await LoadAsync(1);
            }
        }

        private async Task OnLevelChanged(OperationLogLevel? level)
        {
            _level = level;
            await LoadAsync(1);
        }

        private async Task PreviousPageAsync()
        {
            if (_page > 1)
            {
                await LoadAsync(_page - 1);
            }
        }

        private async Task NextPageAsync()
        {
            if (_logs is not null && _page < _logs.TotalPages)
            {
                await LoadAsync(_page + 1);
            }
        }

        private async Task ToggleAsync(int logId)
        {
            if (!_expanded.Remove(logId))
            {
                _expanded.Add(logId);

                if (!_details.ContainsKey(logId))
                {
                    _details[logId] = await OperationLogService.GetDetailsAsync(logId);
                }
            }
        }
    }
}
