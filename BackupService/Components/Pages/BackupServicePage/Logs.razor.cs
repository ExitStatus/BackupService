using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Logs : ComponentBase
    {
        private const int PageSize = 10;

        // Terminal line height in px — must match .log-terminal-line in app.css (the control caps at
        // ~20 of these before scrolling). Used as the Virtualize ItemSize for the detail output.
        private const float LineHeight = 20f;

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        private int _page = 1;
        private PagedResult<OperationLog>? _logs;

        private string _filter = string.Empty;
        private bool _includeMessages;
        private OperationLogLevel? _level;
        private int? _profileId;

        // Level filter options: null = "All levels", then each level.
        private static readonly OperationLogLevel?[] LevelOptions =
            [null, .. Enum.GetValues<OperationLogLevel>().Select(l => (OperationLogLevel?)l)];

        // Profile filter options: null = "All profiles", then each profile id (names looked up below).
        private int?[] _profileOptions = [null];
        private IReadOnlyDictionary<int, string> _profileNames = new Dictionary<int, string>();

        private static string LevelLabel(OperationLogLevel? level) =>
            level.HasValue ? level.Value.GetDescription() : "All levels";

        private string ProfileLabel(int? profileId) =>
            profileId is null ? "All profiles" : _profileNames.GetValueOrDefault(profileId.Value, "(unknown)");

        private bool _hasActiveFilter =>
            !string.IsNullOrWhiteSpace(_filter) || _level is not null || _profileId is not null;

        // Which logs are expanded, and a cache of their detail lines (lazy-loaded on first expand).
        // Stored as List so the terminal view can feed them to <Virtualize Items=...>.
        private readonly HashSet<int> _expanded = [];
        private readonly Dictionary<int, List<OperationLogDetail>> _details = [];

        // Per-expanded-log filters for the detail (terminal) view: a free-text line filter and a
        // level filter, both keyed by log id (each expanded log keeps its own).
        private readonly Dictionary<int, string> _detailFilters = [];
        private readonly Dictionary<int, OperationLogLevel?> _detailLevels = [];

        private string DetailFilterText(int logId) => _detailFilters.GetValueOrDefault(logId, string.Empty);

        private OperationLogLevel? DetailLevel(int logId) => _detailLevels.GetValueOrDefault(logId);

        private void OnDetailFilterChanged(int logId, ChangeEventArgs e) =>
            _detailFilters[logId] = e.Value?.ToString() ?? string.Empty;

        private void ClearDetailFilter(int logId) => _detailFilters[logId] = string.Empty;

        private void OnDetailLevelChanged(int logId, OperationLogLevel? level) =>
            _detailLevels[logId] = level;

        /// <summary>The cached lines for a log, narrowed by its text and level detail filters.</summary>
        private List<OperationLogDetail> FilteredDetails(int logId)
        {
            if (!_details.TryGetValue(logId, out var all))
            {
                return [];
            }

            IEnumerable<OperationLogDetail> query = all;

            var text = _detailFilters.GetValueOrDefault(logId);
            if (!string.IsNullOrWhiteSpace(text))
            {
                query = query.Where(d => d.Message.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (_detailLevels.GetValueOrDefault(logId) is { } level)
            {
                query = query.Where(d => d.Level == level);
            }

            return query.ToList();
        }

        protected override async Task OnInitializedAsync()
        {
            var summaries = await ProfileService.GetSummariesAsync();
            _profileNames = summaries.ToDictionary(s => s.Id, s => s.Name);
            _profileOptions = [null, .. summaries.Select(s => (int?)s.Id)];

            await LoadAsync(_page);
        }

        private async Task LoadAsync(int page)
        {
            _logs = await OperationLogService.GetPageAsync(page, PageSize, _filter, _includeMessages, _level, _profileId);
            _page = _logs.PageNumber;

            // Collapse everything when the page changes — ids on this page differ.
            _expanded.Clear();
        }

        private async Task OnFilterChanged(ChangeEventArgs e)
        {
            _filter = e.Value?.ToString() ?? string.Empty;
            await LoadAsync(1);
        }

        private async Task ClearFilter()
        {
            _filter = string.Empty;
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

        private async Task OnProfileChanged(int? profileId)
        {
            _profileId = profileId;
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
                    var details = await OperationLogService.GetDetailsAsync(logId);
                    _details[logId] = details as List<OperationLogDetail> ?? details.ToList();
                }
            }
        }
    }
}
