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
        // No paging — the log headers live in a scroll panel, so load every matching row on one "page".
        private const int PageSize = int.MaxValue;

        // Lines loaded per window: expanding shows the newest DetailWindowLines of the log's file (the
        // end is where the summary and errors live), and each "Load previous" walks back by the same
        // amount — so a huge log is browsed in bounded chunks rather than loaded whole.
        private const int DetailWindowLines = 1000;

        // Cap on lines a detail search returns (the total match count is still reported).
        private const int MaxSearchMatches = 500;

        // Quiet window after the last keystroke before a detail search runs (each search streams the file).
        private const int SearchDebounceMs = 300;

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        // The live per-profile run status, read once at load to hide the in-progress run's log (only finished
        // runs are shown — a live run is followed in the View Progress dialog). Not subscribed: the page is static.
        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

        private PagedResult<OperationLog>? _logs;
        private bool _disposed;

        // The latest header load (see LoadAsync) and the pending debounced one from typing in the name filter.
        private int _loadVersion;
        private CancellationTokenSource? _loadCts;
        private CancellationTokenSource? _filterDebounce;

        // Logs with a "Load previous" read in flight.
        private readonly HashSet<int> _loadingEarlier = [];

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

        // Which logs are expanded; per expanded log the loaded window of lines (the tail, extended
        // backwards by "Load previous") plus the whole-file facts gathered on the same streaming read:
        // the window's start index, the file's total line count and its Warning/Error counts.
        private readonly HashSet<int> _expanded = [];
        private readonly Dictionary<int, List<OperationLogLine>> _details = [];
        private readonly Dictionary<int, int> _detailStart = [];
        private readonly Dictionary<int, int> _detailTotal = [];
        private readonly Dictionary<int, int> _detailWarnTotal = [];
        private readonly Dictionary<int, int> _detailErrorTotal = [];

        // Per-log search state: free text plus Warning/Error toggles. When any is active the terminal
        // shows a server-side, whole-file search result instead of the loaded window (grep semantics —
        // with thousands of lines users search, they don't scroll). Text input is debounced because
        // every search streams the log's file.
        private readonly Dictionary<int, string> _detailFilters = [];
        private readonly HashSet<int> _detailWarning = [];
        private readonly HashSet<int> _detailError = [];
        private readonly Dictionary<int, OperationLogSearch> _searchResults = [];
        private readonly Dictionary<int, CancellationTokenSource> _searchDebounce = [];

        private string DetailFilterText(int logId) => _detailFilters.GetValueOrDefault(logId, string.Empty);

        private bool DetailWarningChecked(int logId) => _detailWarning.Contains(logId);

        private bool DetailErrorChecked(int logId) => _detailError.Contains(logId);

        // Whole-file level counts (from the window read), shown beside the checkbox labels.
        private int WarningCount(int logId) => _detailWarnTotal.GetValueOrDefault(logId);

        private int ErrorCount(int logId) => _detailErrorTotal.GetValueOrDefault(logId);

        private int DetailStart(int logId) => _detailStart.GetValueOrDefault(logId);

        private int DetailTotal(int logId) => _detailTotal.GetValueOrDefault(logId);

        /// <summary>How many earlier lines the next "Load previous" would fetch.</summary>
        private int EarlierChunk(int logId) => Math.Min(DetailWindowLines, DetailStart(logId));

        /// <summary>Whether a text or level filter is active — the terminal then shows search results.</summary>
        private bool SearchActive(int logId) =>
            !string.IsNullOrWhiteSpace(DetailFilterText(logId)) || _detailWarning.Contains(logId) || _detailError.Contains(logId);

        /// <summary>A search is active but its (debounced) result hasn't arrived yet.</summary>
        private bool SearchPending(int logId) => SearchActive(logId) && !_searchResults.ContainsKey(logId);

        /// <summary>The lines the terminal shows: search results while a filter is active, else the window.</summary>
        private IReadOnlyList<OperationLogLine> DisplayedLines(int logId)
        {
            if (SearchActive(logId))
            {
                return _searchResults.TryGetValue(logId, out var search) ? search.Matches : [];
            }

            return _details.TryGetValue(logId, out var lines) ? lines : [];
        }

        private string SearchStatus(int logId)
        {
            if (!_searchResults.TryGetValue(logId, out var search))
            {
                return "Searching…";
            }

            var noun = search.TotalMatches == 1 ? "match" : "matches";
            var status = $"{search.TotalMatches:N0} {noun} in {DetailTotal(logId):N0} lines";
            return search.Truncated ? $"{status} — showing the first {MaxSearchMatches:N0}" : status;
        }

        private string WindowStatus(int logId) =>
            $"Showing the last {(_details.TryGetValue(logId, out var lines) ? lines.Count : 0):N0} of {DetailTotal(logId):N0} lines";

        private void OnDetailFilterChanged(int logId, ChangeEventArgs e)
        {
            _detailFilters[logId] = e.Value?.ToString() ?? string.Empty;
            DebounceSearch(logId);
        }

        private async Task ClearDetailFilterAsync(int logId)
        {
            _detailFilters[logId] = string.Empty;
            await RunSearchAsync(logId);
        }

        private async Task OnDetailWarningChangedAsync(int logId, ChangeEventArgs e)
        {
            Toggle(_detailWarning, logId, e.Value is true);
            await RunSearchAsync(logId);
        }

        private async Task OnDetailErrorChangedAsync(int logId, ChangeEventArgs e)
        {
            Toggle(_detailError, logId, e.Value is true);
            await RunSearchAsync(logId);
        }

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

        /// <summary>
        /// (Re)arms the per-log debounce and runs the search after the quiet window — so typing doesn't
        /// scan the file on every keystroke. A newer keystroke cancels the pending run.
        /// </summary>
        private void DebounceSearch(int logId)
        {
            if (_searchDebounce.Remove(logId, out var previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var cts = new CancellationTokenSource();
            _searchDebounce[logId] = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SearchDebounceMs, token);
                    await InvokeAsync(async () =>
                    {
                        if (!_disposed && !token.IsCancellationRequested)
                        {
                            await RunSearchAsync(logId);
                            StateHasChanged();
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer keystroke, or the page was disposed.
                }
            });
        }

        /// <summary>
        /// Runs (or clears) the server-side search for one log from its current filter state. With no
        /// active filter the result is dropped and the terminal falls back to the loaded window.
        /// </summary>
        private async Task RunSearchAsync(int logId)
        {
            if (!SearchActive(logId))
            {
                _searchResults.Remove(logId);
                return;
            }

            List<OperationLogLevel>? levels = null;
            if (_detailWarning.Contains(logId) || _detailError.Contains(logId))
            {
                levels = [];
                if (_detailWarning.Contains(logId))
                {
                    levels.Add(OperationLogLevel.Warning);
                }
                if (_detailError.Contains(logId))
                {
                    levels.Add(OperationLogLevel.Error);
                }
            }

            _searchResults[logId] = await OperationLogService.SearchDetailsAsync(
                logId, DetailFilterText(logId), levels, MaxSearchMatches);
        }

        protected override async Task OnInitializedAsync()
        {
            var summaries = await ProfileService.GetSummariesAsync();
            _profileNames = summaries.ToDictionary(s => s.Id, s => s.Name);
            _profileOptions = [null, .. summaries.Select(s => (int?)s.Id)];

            await LoadAsync();
        }

        // Only the latest load is applied: with "Include Messages" a load scans every log file, so an earlier,
        // slower one could finish last and overwrite newer results (a cleared filter then still showed the old
        // matches). Each load cancels the one before and remembers its number.
        private async Task LoadAsync()
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            var cts = _loadCts = new CancellationTokenSource();
            var version = ++_loadVersion;

            PagedResult<OperationLog> page;
            try
            {
                page = await OperationLogService.GetPageAsync(1, PageSize, _filter, _includeMessages, _level, _profileId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // superseded by a newer load
            }
            if (version != _loadVersion || _disposed)
            {
                return;
            }

            _logs = ExcludeInProgressRuns(page);

            // Collapse everything when the (re)load happens — the visible set may differ — releasing all
            // cached terminal data.
            _expanded.Clear();
            ClearAllDetailState();
        }

        // Typing in the name filter waits for a pause, so a word doesn't start one load per letter — each of which,
        // with "Include Messages", scans every log file.
        private void ScheduleLoad()
        {
            _filterDebounce?.Cancel();
            _filterDebounce?.Dispose();
            var cts = _filterDebounce = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SearchDebounceMs, cts.Token);
                    await InvokeAsync(async () =>
                    {
                        if (!cts.IsCancellationRequested && !_disposed)
                        {
                            await LoadAsync();
                            StateHasChanged();
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer keystroke, or the page was disposed.
                }
            });
        }

        private void CancelScheduledLoad()
        {
            _filterDebounce?.Cancel();
            _filterDebounce?.Dispose();
            _filterDebounce = null;
        }

        // Hides the in-progress run's log — exactly the one its handler recorded — so the Logs page shows only
        // finished runs; the live run is followed in the View Progress dialog instead. (It used to hide "the newest
        // log of a running profile", which under a level filter, or for a run still queued with no log yet, was an
        // older, finished run's.) A page's rows are the full result set (PageSize is unbounded), so a simple
        // in-memory filter suffices.
        private PagedResult<OperationLog> ExcludeInProgressRuns(PagedResult<OperationLog> page)
        {
            var inProgress = page.Items
                .Where(l => l.Profile is not null)
                .Select(l => l.Profile!.Id)
                .Distinct()
                .Select(StatusService.GetRunLog)
                .OfType<int>()
                .ToHashSet();

            if (inProgress.Count == 0)
            {
                return page;
            }

            var items = page.Items.Where(l => !inProgress.Contains(l.Id)).ToList();
            return new PagedResult<OperationLog>(items, items.Count, page.PageNumber, page.PageSize);
        }

        private void OnFilterChanged(ChangeEventArgs e)
        {
            _filter = e.Value?.ToString() ?? string.Empty;
            ScheduleLoad();
        }

        private async Task ClearFilter()
        {
            CancelScheduledLoad();
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
            if (_expanded.Remove(logId))
            {
                // Collapsed — release the loaded lines (and this log's view state) so the terminal's
                // data isn't held in memory while it isn't showing. Re-expanding reloads from the file.
                ReleaseDetailState(logId);
                return;
            }

            _expanded.Add(logId);
            var version = _loadVersion;

            // Load the tail window — the end of the log is where the summary and errors live.
            var window = await OperationLogService.GetDetailWindowAsync(logId, skip: null, take: DetailWindowLines);

            // Collapsed again (or the list reloaded) while that read ran: keeping the lines would hold them for a log
            // that isn't showing.
            if (version == _loadVersion && _expanded.Contains(logId) && !_disposed)
            {
                ApplyWindow(logId, window);
            }
        }

        /// <summary>Walks the window backwards: prepends the previous chunk of earlier lines.</summary>
        private async Task LoadEarlierAsync(int logId)
        {
            var start = DetailStart(logId);
            // One walk-back at a time per log: a double-click otherwise read the same chunk twice and prepended both.
            if (start <= 0 || !_details.TryGetValue(logId, out var lines) || !_loadingEarlier.Add(logId))
            {
                return;
            }

            try
            {
                var skip = Math.Max(0, start - DetailWindowLines);
                var window = await OperationLogService.GetDetailWindowAsync(logId, skip, take: start - skip);

                // Only if this log still shows the same lines (not collapsed or reloaded meanwhile).
                if (_details.TryGetValue(logId, out var current) && ReferenceEquals(current, lines))
                {
                    lines.InsertRange(0, window.Lines);
                    _detailStart[logId] = window.StartIndex;
                }
            }
            finally
            {
                _loadingEarlier.Remove(logId);
            }
        }

        private bool LoadingEarlier(int logId) => _loadingEarlier.Contains(logId);

        private void ApplyWindow(int logId, OperationLogWindow window)
        {
            _details[logId] = window.Lines as List<OperationLogLine> ?? window.Lines.ToList();
            _detailStart[logId] = window.StartIndex;
            _detailTotal[logId] = window.TotalCount;
            _detailWarnTotal[logId] = window.WarningCount;
            _detailErrorTotal[logId] = window.ErrorCount;
        }

        /// <summary>Drops the cached lines, search state and per-log view state for one log (on collapse),
        /// so the potentially large line list is freed.</summary>
        private void ReleaseDetailState(int logId)
        {
            _details.Remove(logId);
            _detailStart.Remove(logId);
            _detailTotal.Remove(logId);
            _detailWarnTotal.Remove(logId);
            _detailErrorTotal.Remove(logId);
            _detailFilters.Remove(logId);
            _detailWarning.Remove(logId);
            _detailError.Remove(logId);
            _searchResults.Remove(logId);
            if (_searchDebounce.Remove(logId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        /// <summary>Releases every log's cached lines and view state (e.g. when the filter changes and
        /// all rows collapse).</summary>
        private void ClearAllDetailState()
        {
            _details.Clear();
            _detailStart.Clear();
            _detailTotal.Clear();
            _detailWarnTotal.Clear();
            _detailErrorTotal.Clear();
            _detailFilters.Clear();
            _detailWarning.Clear();
            _detailError.Clear();
            _searchResults.Clear();
            foreach (var cts in _searchDebounce.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _searchDebounce.Clear();
        }

        // Tearing down the component (e.g. selecting another sidebar panel, or navigating away from the
        // Logs page) releases any loaded log data so it isn't retained after the page closes.
        public void Dispose()
        {
            _disposed = true;
            CancelScheduledLoad();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            _expanded.Clear();
            ClearAllDetailState();
            _logs = null;
        }
    }
}
