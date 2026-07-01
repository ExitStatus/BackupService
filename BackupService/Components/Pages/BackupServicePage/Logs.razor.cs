using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Logs : ComponentBase, IDisposable
    {
        // No paging — the log headers live in a scroll panel, so load every matching row on one "page".
        private const int PageSize = int.MaxValue;

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

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

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

        // Per-expanded-log filters for the detail (terminal) view: a free-text line filter, plus
        // Warning/Error level toggles. All keyed by log id (each expanded log keeps its own). The two
        // level sets hold the log ids whose Warning / Error checkbox is currently ticked; both unticked
        // means no level filter (every line shown).
        private readonly Dictionary<int, string> _detailFilters = [];
        private readonly HashSet<int> _detailWarning = [];
        private readonly HashSet<int> _detailError = [];

        // Log ids whose terminal has "Pause scrolling" ticked — a refresh that adds lines won't
        // auto-scroll them to the bottom.
        private readonly HashSet<int> _detailPaused = [];

        // Log ids whose terminal grew on the last refresh and should be scrolled to the bottom after the
        // next render (auto-scroll to follow a running backup); drained in OnAfterRenderAsync.
        private readonly List<int> _scrollQueue = [];

        private string DetailFilterText(int logId) => _detailFilters.GetValueOrDefault(logId, string.Empty);

        private bool DetailWarningChecked(int logId) => _detailWarning.Contains(logId);

        private bool DetailErrorChecked(int logId) => _detailError.Contains(logId);

        private bool DetailPausedChecked(int logId) => _detailPaused.Contains(logId);

        // The number of cached lines at each level, shown beside the checkbox label.
        private int WarningCount(int logId) => DetailLevelCount(logId, OperationLogLevel.Warning);

        private int ErrorCount(int logId) => DetailLevelCount(logId, OperationLogLevel.Error);

        private int DetailLevelCount(int logId, OperationLogLevel level) =>
            _details.TryGetValue(logId, out var all) ? all.Count(d => d.Level == level) : 0;

        private void OnDetailFilterChanged(int logId, ChangeEventArgs e) =>
            _detailFilters[logId] = e.Value?.ToString() ?? string.Empty;

        private void ClearDetailFilter(int logId) => _detailFilters[logId] = string.Empty;

        private void OnDetailWarningChanged(int logId, ChangeEventArgs e) => Toggle(_detailWarning, logId, e.Value is true);

        private void OnDetailErrorChanged(int logId, ChangeEventArgs e) => Toggle(_detailError, logId, e.Value is true);

        private void OnDetailPausedChanged(int logId, ChangeEventArgs e) => Toggle(_detailPaused, logId, e.Value is true);

        private static void Toggle(HashSet<int> set, int logId, bool on)
        {
            if (on)
            {
                set.Add(logId);
            }
            else
            {
                set.Remove(logId);
            }
        }

        /// <summary>The cached lines for a log, narrowed by its text filter and Warning/Error toggles.</summary>
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

            // Warning/Error toggles are additive: with either ticked, show only those levels; with
            // neither ticked, show every level.
            var warning = _detailWarning.Contains(logId);
            var error = _detailError.Contains(logId);
            if (warning || error)
            {
                query = query.Where(d =>
                    (warning && d.Level == OperationLogLevel.Warning) ||
                    (error && d.Level == OperationLogLevel.Error));
            }

            return query.ToList();
        }

        protected override async Task OnInitializedAsync()
        {
            var summaries = await ProfileService.GetSummariesAsync();
            _profileNames = summaries.ToDictionary(s => s.Id, s => s.Name);
            _profileOptions = [null, .. summaries.Select(s => (int?)s.Id)];

            await LoadAsync();
        }

        // Subscribe once the component is live (OnAfterRender doesn't run during prerender, so we only
        // attach in the interactive circuit). The watcher debounces, so we just refresh on each push.
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                LogWatcher.Changed += OnLogsChanged;
                _subscribed = true;
            }

            // Auto-scroll any expanded terminal that gained lines on the last refresh. Runs after render
            // so the new rows (and the grown scroll height) exist in the DOM; drained so it fires once.
            if (_scrollQueue.Count > 0)
            {
                var ids = _scrollQueue.ToArray();
                _scrollQueue.Clear();

                foreach (var id in ids)
                {
                    try
                    {
                        await JS.InvokeVoidAsync("scrollLogTerminalToBottom", $"log-terminal-{id}");
                    }
                    catch
                    {
                        // Best-effort — the terminal may have collapsed or the circuit torn down.
                    }
                }
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
                _logs = await OperationLogService.GetPageAsync(1, PageSize, _filter, _includeMessages, _level, _profileId);

                // Drop expansion state for logs no longer in the result (e.g. filtered out); refresh the
                // details of those still shown so a running log stays live.
                var visibleIds = _logs.Items.Select(l => l.Id).ToHashSet();
                _expanded.RemoveWhere(id => !visibleIds.Contains(id));

                foreach (var id in _expanded)
                {
                    var oldCount = _details.TryGetValue(id, out var existing) ? existing.Count : 0;
                    var details = await OperationLogService.GetDetailsAsync(id);
                    var list = details as List<OperationLogDetail> ?? details.ToList();
                    _details[id] = list;

                    // New lines arrived — follow them to the bottom unless the user paused this terminal.
                    if (list.Count > oldCount && !_detailPaused.Contains(id))
                    {
                        _scrollQueue.Add(id);
                    }
                }

                StateHasChanged();
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async Task LoadAsync()
        {
            _logs = await OperationLogService.GetPageAsync(1, PageSize, _filter, _includeMessages, _level, _profileId);

            // Collapse everything when the filter changes — the visible set differs.
            _expanded.Clear();
        }

        private async Task OnFilterChanged(ChangeEventArgs e)
        {
            _filter = e.Value?.ToString() ?? string.Empty;
            await LoadAsync();
        }

        private async Task ClearFilter()
        {
            _filter = string.Empty;
            await LoadAsync();
        }

        private async Task OnIncludeMessagesChanged(ChangeEventArgs e)
        {
            _includeMessages = e.Value is true;

            // Only re-query if a filter is active — the checkbox has no effect without one.
            if (!string.IsNullOrWhiteSpace(_filter))
            {
                await LoadAsync();
            }
        }

        private async Task OnLevelChanged(OperationLogLevel? level)
        {
            _level = level;
            await LoadAsync();
        }

        private async Task OnProfileChanged(int? profileId)
        {
            _profileId = profileId;
            await LoadAsync();
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
