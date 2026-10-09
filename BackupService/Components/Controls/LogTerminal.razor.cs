using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// A self-contained terminal view of one operation log's lines (a "tail"), with the same free-text +
    /// Warning/Error + Pause-scrolling filters as the Logs page. Live-refreshes on <see cref="ILogWatcher"/>
    /// pushes and auto-scrolls to the bottom (unless paused) so it follows a running backup. Used by the
    /// View Progress dialog; the Logs page keeps its own inline copy.
    /// </summary>
    public partial class LogTerminal : ComponentBase, IDisposable
    {
        [Inject]
        private IOperationLogService OperationLogService { get; set; } = default!;

        [Inject]
        private ILogWatcher LogWatcher { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        // Only the most recent lines are held/shown, to bound memory on a large, still-growing log.
        private const int MaxLines = 500;

        /// <summary>The operation log whose lines are shown.</summary>
        [Parameter]
        public int LogId { get; set; }

        private List<OperationLogLine>? _lines;
        private string _filterText = string.Empty;
        private bool _warning;
        private bool _error;
        private bool _paused;

        private bool _subscribed;
        private bool _disposed;
        private bool _refreshing;
        private bool _refreshAgain; // a push arrived during a refresh — read once more after it
        private bool _scrollPending;

        private string TerminalElementId => $"log-terminal-view-{LogId}";

        protected override async Task OnInitializedAsync() => await LoadAsync();

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                LogWatcher.Changed += OnLogsChanged;
                _subscribed = true;
            }

            if (_scrollPending)
            {
                _scrollPending = false;
                try
                {
                    await JS.InvokeVoidAsync("scrollLogTerminalToBottom", TerminalElementId);
                }
                catch
                {
                    // Best-effort — the dialog may have closed or the circuit torn down.
                }
            }
        }

        private async Task LoadAsync()
        {
            var details = await OperationLogService.GetRecentDetailsAsync(LogId, MaxLines);
            _lines = details as List<OperationLogLine> ?? details.ToList();
            _scrollPending = !_paused;
        }

        private void OnLogsChanged()
        {
            // Raised on a background thread — marshal onto the renderer and reload in place.
            _ = InvokeAsync(async () =>
            {
                if (_disposed)
                {
                    return;
                }
                if (_refreshing)
                {
                    // Not dropped: if it was the run's last push (its final lines and summary), nothing else would
                    // trigger a read. The refresh in flight reads once more when it finishes.
                    _refreshAgain = true;
                    return;
                }
                _refreshing = true;
                try
                {
                    do
                    {
                        _refreshAgain = false;
                        // Compare the last line (not the count) to detect new content — the count plateaus at
                        // MaxLines once the log is capped, so growth would otherwise stop being noticed.
                        var oldLast = _lines is { Count: > 0 } ? _lines[^1] : null;
                        var details = await OperationLogService.GetRecentDetailsAsync(LogId, MaxLines);
                        _lines = details as List<OperationLogLine> ?? details.ToList();
                        var newLast = _lines.Count > 0 ? _lines[^1] : null;
                        if (!_paused && newLast is not null && !newLast.Equals(oldLast))
                        {
                            _scrollPending = true; // follow the new lines to the bottom
                        }
                        StateHasChanged();
                    }
                    while (_refreshAgain && !_disposed);
                }
                catch
                {
                    // Best-effort refresh; the next push will try again.
                }
                finally
                {
                    _refreshing = false;
                }
            });
        }

        private void OnFilterChanged(ChangeEventArgs e) => _filterText = e.Value?.ToString() ?? string.Empty;

        private void ClearFilter() => _filterText = string.Empty;

        private int LevelCount(OperationLogLevel level) => _lines?.Count(l => l.Level == level) ?? 0;

        // The cached lines narrowed by the free-text filter and the Warning/Error toggles (additive: with
        // either ticked, show only those levels; with neither, show every level).
        private List<OperationLogLine> FilteredLines()
        {
            if (_lines is null)
            {
                return [];
            }

            IEnumerable<OperationLogLine> query = _lines;

            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                query = query.Where(l => l.Message.Contains(_filterText.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            if (_warning || _error)
            {
                query = query.Where(l =>
                    (_warning && l.Level == OperationLogLevel.Warning) ||
                    (_error && l.Level == OperationLogLevel.Error));
            }

            return query.ToList();
        }

        public void Dispose()
        {
            _disposed = true;
            if (_subscribed)
            {
                LogWatcher.Changed -= OnLogsChanged;
            }
            _lines = null;
        }
    }
}
