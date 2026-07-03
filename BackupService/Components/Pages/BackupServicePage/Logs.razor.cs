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

        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        // The live per-profile run status, read once at load to hide the in-progress run's log (only finished
        // runs are shown — a live run is followed in the View Progress dialog). Not subscribed: the page is static.
        [Inject]
        private IProfileStatusService StatusService { get; set; } = default!;

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

        // Which logs are expanded, and the loaded lines of the currently-expanded ones (loaded lazily on
        // expand, released on collapse — see ReleaseDetailState — so only open terminals hold their data).
        private readonly HashSet<int> _expanded = [];
        private readonly Dictionary<int, List<OperationLogLine>> _details = [];

        // Per-expanded-log filters for the detail (terminal) view: a free-text line filter, plus
        // Warning/Error level toggles. All keyed by log id (each expanded log keeps its own). The two
        // level sets hold the log ids whose Warning / Error checkbox is currently ticked; both unticked
        // means no level filter (every line shown).
        private readonly Dictionary<int, string> _detailFilters = [];
        private readonly HashSet<int> _detailWarning = [];
        private readonly HashSet<int> _detailError = [];

        private string DetailFilterText(int logId) => _detailFilters.GetValueOrDefault(logId, string.Empty);

        private bool DetailWarningChecked(int logId) => _detailWarning.Contains(logId);

        private bool DetailErrorChecked(int logId) => _detailError.Contains(logId);

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
        private List<OperationLogLine> FilteredDetails(int logId)
        {
            if (!_details.TryGetValue(logId, out var all))
            {
                return [];
            }

            IEnumerable<OperationLogLine> query = all;

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

        private async Task LoadAsync()
        {
            var page = await OperationLogService.GetPageAsync(1, PageSize, _filter, _includeMessages, _level, _profileId);
            _logs = ExcludeInProgressRuns(page);

            // Collapse everything when the (re)load happens — the visible set may differ — releasing all
            // cached terminal data.
            _expanded.Clear();
            ClearAllDetailState();
        }

        // Hides the in-progress run's log — the newest log of any profile currently running — so the Logs page
        // shows only finished runs. A page's rows are the full result set (PageSize is unbounded), so a simple
        // in-memory filter suffices; the live run is followed in the View Progress dialog instead.
        private PagedResult<OperationLog> ExcludeInProgressRuns(PagedResult<OperationLog> page)
        {
            var inProgress = page.Items
                .Where(l => l.Profile is not null && StatusService.Get(l.Profile.Id) == ProfileStatus.Running)
                .GroupBy(l => l.Profile!.Id)
                .Select(g => g.Max(l => l.Id))
                .ToHashSet();

            if (inProgress.Count == 0)
            {
                return page;
            }

            var items = page.Items.Where(l => !inProgress.Contains(l.Id)).ToList();
            return new PagedResult<OperationLog>(items, items.Count, page.PageNumber, page.PageSize);
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
            if (_expanded.Remove(logId))
            {
                // Collapsed — release the loaded lines (and this log's view state) so the terminal's
                // data isn't held in memory while it isn't showing. Re-expanding reloads from the file.
                ReleaseDetailState(logId);
                return;
            }

            _expanded.Add(logId);

            // Loaded lazily, only on expand.
            var details = await OperationLogService.GetDetailsAsync(logId);
            _details[logId] = details as List<OperationLogLine> ?? details.ToList();
        }

        /// <summary>Drops the cached lines and per-log view state for one log (on collapse), so the
        /// potentially large line list is freed.</summary>
        private void ReleaseDetailState(int logId)
        {
            _details.Remove(logId);
            _detailFilters.Remove(logId);
            _detailWarning.Remove(logId);
            _detailError.Remove(logId);
        }

        /// <summary>Releases every log's cached lines and view state (e.g. when the filter changes and
        /// all rows collapse).</summary>
        private void ClearAllDetailState()
        {
            _details.Clear();
            _detailFilters.Clear();
            _detailWarning.Clear();
            _detailError.Clear();
        }

        // Tearing down the component (e.g. selecting another sidebar panel, or navigating away from the
        // Logs page) releases any loaded log data so it isn't retained after the page closes.
        public void Dispose()
        {
            _expanded.Clear();
            ClearAllDetailState();
            _logs = null;
        }
    }
}
