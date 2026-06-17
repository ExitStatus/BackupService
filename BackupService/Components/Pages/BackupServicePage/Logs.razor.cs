using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Logs : ComponentBase, IDisposable
    {
        private const int PageSize = 10;

        // Terminal line height in px — must match .log-terminal-line in app.css (the control caps at
        // ~20 of these before scrolling). Used as the Virtualize ItemSize for the detail output.
        private const float LineHeight = 20f;

        private bool _refreshing;
        private bool _subscribed;
        private bool _disposed;

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        // Pushes a refresh whenever log data changes (debounced); replaces interval polling.
        [Inject]
        private ILogWatcher LogWatcher { get; set; } = default!;

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

        // Subscribe once the component is live (OnAfterRender doesn't run during prerender, so we only
        // attach in the interactive circuit). The watcher debounces, so we just refresh on each push.
        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                LogWatcher.Changed += OnLogsChanged;
                _subscribed = true;
            }
        }

        private void OnLogsChanged()
        {
            // Raised on a background thread — marshal onto the renderer and refresh. Swallow failures so
            // a transient error (or a torn-down circuit) can't escape onto the thread pool.
            _ = InvokeAsync(async () =>
            {
                try
                {
                    await RefreshAsync();
                }
                catch
                {
                    // Best-effort refresh; the next notification will try again.
                }
            });
        }

        /// <summary>
        /// Re-reads the current page (and any expanded logs' details) in place, without collapsing the
        /// user's expanded rows or resetting their filters/page. Driven by the log watcher's push.
        /// </summary>
        private async Task RefreshAsync()
        {
            if (_disposed || _refreshing)
            {
                return;
            }

            _refreshing = true;
            try
            {
                _logs = await OperationLogService.GetPageAsync(_page, PageSize, _filter, _includeMessages, _level, _profileId);
                _page = _logs.PageNumber;

                // Drop expansion state for logs that have scrolled off this page (e.g. pushed down by
                // newer entries); refresh the details of those still shown so a running log stays live.
                var visibleIds = _logs.Items.Select(l => l.Id).ToHashSet();
                _expanded.RemoveWhere(id => !visibleIds.Contains(id));

                foreach (var id in _expanded)
                {
                    var details = await OperationLogService.GetDetailsAsync(id);
                    _details[id] = details as List<OperationLogDetail> ?? details.ToList();
                }

                StateHasChanged();
            }
            finally
            {
                _refreshing = false;
            }
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

        // Tearing down the component (e.g. selecting another sidebar panel) unsubscribes, so the watcher
        // no longer pushes refreshes to this instance.
        public void Dispose()
        {
            _disposed = true;
            if (_subscribed)
            {
                LogWatcher.Changed -= OnLogsChanged;
            }
        }
    }
}
