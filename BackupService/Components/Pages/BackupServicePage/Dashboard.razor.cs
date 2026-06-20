using ApexCharts;
using BackupService.Dashboard;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Profiles;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Pages.BackupServicePage
{
    public partial class Dashboard : ComponentBase, IDisposable
    {
        // Theme colours for the charts (ApexCharts JS options can't read CSS custom properties, so the
        // palette is mirrored here as literals — green/amber/red matching app.css).
        private const string SuccessColor = "#2e7d32";
        private const string WarningColor = "#c9821a";
        private const string DangerColor = "#ff6b6b";
        private const string AccentColor = "#4f8cff";

        // JS formatter that auto-scales a byte count to B/KB/MB/GB/TB (used for the data-volume y-axis
        // and its tooltip). Blazor-ApexCharts emits a "function(...)" string as a real JS function.
        private const string BytesFormatter =
            "function (val) { if (val == null) return ''; var u = ['B','KB','MB','GB','TB','PB']; var i = 0; var n = Math.abs(val); while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; } return (i === 0 ? n : n.toFixed(n < 10 ? 1 : 0)) + ' ' + u[i]; }";

        [Inject]
        private IDashboardService DashboardService { get; set; } = default!;

        [Inject]
        private IProfileService ProfileService { get; set; } = default!;

        // Live "running now" count comes from the in-memory status tracker.
        [Inject]
        private IProfileStatusService ProfileStatusService { get; set; } = default!;

        // Pushes a refresh whenever log data changes (a run completes); reused from the Logs panel.
        [Inject]
        private ILogWatcher LogWatcher { get; set; } = default!;

        private DashboardData? _data;
        private int _days = 14;
        private List<int> _profileIds = [];

        private bool _refreshing;
        private bool _subscribed;
        private bool _disposed;

        private static readonly int[] PeriodOptions = [7, 14, 30];
        private static string PeriodLabel(int days) => $"Last {days} days";

        private ApexChart<DailyOutcome>? _outcomesChart;
        private ApexChart<DailyBytes>? _bytesChart;
        private ApexChart<ProfileDuration>? _durationChart;
        private ApexChart<OutcomeSlice>? _donutChart;

        private ApexChartOptions<DailyOutcome> _outcomesOptions = default!;
        private ApexChartOptions<DailyBytes> _bytesOptions = default!;
        private ApexChartOptions<ProfileDuration> _durationOptions = default!;
        private ApexChartOptions<OutcomeSlice> _donutOptions = default!;

        private IReadOnlyList<OutcomeSlice> _slices = [];

        private int RunningNow => _profileIds.Count(ProfileStatusService.IsRunning);

        // Horizontal bars need vertical room per profile.
        private int DurationChartHeight => Math.Max(200, (_data?.DurationByProfile.Count ?? 0) * 40 + 60);

        private string LastRunText => _data?.LastRunUtc is { } last
            ? last.ToLocalTime().ToString("dd MMM HH:mm")
            : "—";

        protected override async Task OnInitializedAsync()
        {
            BuildChartOptions();
            await LoadAsync();
        }

        // Subscribe once the component is live (OnAfterRender doesn't run during prerender).
        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                LogWatcher.Changed += OnDataChanged;
                ProfileStatusService.Changed += OnStatusChanged;
                _subscribed = true;
            }
        }

        private async Task LoadAsync()
        {
            _data = await DashboardService.GetAsync(_days);
            _slices = BuildSlices(_data);

            var summaries = await ProfileService.GetSummariesAsync();
            _profileIds = summaries.Select(s => s.Id).ToList();
        }

        private async Task OnPeriodChanged(int days)
        {
            _days = days;
            await LoadAsync();
            StateHasChanged();
            await UpdateChartsAsync();
        }

        private void OnDataChanged()
        {
            // Raised on a background thread — marshal onto the renderer. Swallow failures so a transient
            // error (or a torn-down circuit) can't escape onto the thread pool.
            _ = InvokeAsync(async () =>
            {
                try
                {
                    await RefreshAsync();
                }
                catch
                {
                    // Best-effort; the next push will try again.
                }
            });
        }

        private void OnStatusChanged(int profileId) =>
            _ = InvokeAsync(StateHasChanged); // just refresh the "running now" count

        private async Task RefreshAsync()
        {
            if (_disposed || _refreshing)
            {
                return;
            }

            _refreshing = true;
            try
            {
                await LoadAsync();
                StateHasChanged();
                await UpdateChartsAsync();
            }
            finally
            {
                _refreshing = false;
            }
        }

        // Push the (re-bound) series data into the already-rendered charts. Guarded — the chart refs are
        // null until first rendered, and a chart may not exist when its period has no data.
        private async Task UpdateChartsAsync()
        {
            await TryUpdate(_outcomesChart);
            await TryUpdate(_bytesChart);
            await TryUpdate(_durationChart);
            await TryUpdate(_donutChart);

            static async Task TryUpdate<T>(ApexChart<T>? chart) where T : class
            {
                if (chart is null)
                {
                    return;
                }
                try
                {
                    await chart.UpdateSeriesAsync(true);
                }
                catch
                {
                    // A chart that isn't initialised yet (or was just removed) — ignore.
                }
            }
        }

        private static IReadOnlyList<OutcomeSlice> BuildSlices(DashboardData d) =>
        [
            new("Success", d.TotalSuccess),
            new("Warnings", d.TotalCompletedWithWarnings),
            new("Errors", d.TotalCompletedWithErrors + d.TotalFailed),
        ];

        private void BuildChartOptions()
        {
            _outcomesOptions = new ApexChartOptions<DailyOutcome>
            {
                Theme = new Theme { Mode = Mode.Dark },
                Chart = new Chart { Stacked = true, Background = "transparent", Toolbar = new Toolbar { Show = false } },
                Colors = [SuccessColor, WarningColor, DangerColor],
                PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { ColumnWidth = "60%", BorderRadius = 3 } },
                DataLabels = new ApexCharts.DataLabels { Enabled = false },
                Legend = new Legend { Position = LegendPosition.Top },
            };

            _bytesOptions = new ApexChartOptions<DailyBytes>
            {
                Theme = new Theme { Mode = Mode.Dark },
                Chart = new Chart { Background = "transparent", Toolbar = new Toolbar { Show = false } },
                Colors = [AccentColor],
                PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { ColumnWidth = "60%", BorderRadius = 3 } },
                DataLabels = new ApexCharts.DataLabels { Enabled = false },
                Legend = new Legend { Show = false },
                // Auto-scaled byte units on the y-axis (and in the tooltip).
                Yaxis = [new YAxis { Labels = new YAxisLabels { Formatter = BytesFormatter } }],
                Tooltip = new Tooltip { Y = new TooltipY { Formatter = BytesFormatter } },
            };

            _durationOptions = new ApexChartOptions<ProfileDuration>
            {
                Theme = new Theme { Mode = Mode.Dark },
                Chart = new Chart { Stacked = true, Background = "transparent", Toolbar = new Toolbar { Show = false } },
                Colors = [SuccessColor, WarningColor, DangerColor],
                PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = true, BorderRadius = 3 } },
                DataLabels = new ApexCharts.DataLabels { Enabled = false },
                Legend = new Legend { Position = LegendPosition.Top },
            };

            _donutOptions = new ApexChartOptions<OutcomeSlice>
            {
                Theme = new Theme { Mode = Mode.Dark },
                Chart = new Chart { Background = "transparent" },
                Colors = [SuccessColor, WarningColor, DangerColor],
                Legend = new Legend { Position = LegendPosition.Bottom },
            };
        }

        private static string FormatDuration(long ms) =>
            ms >= 1000 ? $"{ms / 1000.0:0.##}s" : $"{ms}ms";

        private static string OutcomeClass(RunOutcome outcome) => outcome switch
        {
            RunOutcome.Success => "log-level-info",            // green
            RunOutcome.CompletedWithWarnings => "log-level-warning", // amber
            _ => "log-level-error",                            // red — completed-with-errors + failed
        };

        public void Dispose()
        {
            _disposed = true;
            if (_subscribed)
            {
                LogWatcher.Changed -= OnDataChanged;
                ProfileStatusService.Changed -= OnStatusChanged;
            }
        }
    }

    /// <summary>A slice of the overall-outcome donut (label + count).</summary>
    public sealed record OutcomeSlice(string Label, int Count);
}
