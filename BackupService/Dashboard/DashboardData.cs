using BackupService.Enumerations;

namespace BackupService.Dashboard
{
    /// <summary>
    /// Everything the dashboard renders, computed in one pass by <see cref="IDashboardService"/> over the
    /// run history for the selected period. (The live "running now" count is read separately from the
    /// in-memory <c>IProfileStatusService</c> by the component.)
    /// </summary>
    public sealed record DashboardData(
        int PeriodDays,
        int TotalProfiles,
        int EnabledProfiles,
        int DisabledProfiles,
        int RunsInPeriod,
        double SuccessRatePercent,
        int TotalSuccess,
        int TotalCompletedWithErrors,
        int TotalFailed,
        long FilesSyncedInPeriod,
        DateTimeOffset? LastRunUtc,
        IReadOnlyList<DailyOutcome> OutcomesByDay,
        IReadOnlyList<ProfileDuration> DurationByProfile,
        IReadOnlyList<RecentRun> RecentRuns)
    {
        public static DashboardData Empty(int periodDays) =>
            new(periodDays, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, [], [], []);
    }

    /// <summary>One day's run-outcome tally (for the stacked-column "runs over time" chart).</summary>
    public sealed record DailyOutcome(DateOnly Date, int Success, int CompletedWithErrors, int Failed);

    /// <summary>
    /// A profile's average run time split into a success-weighted and a failure-weighted portion
    /// (so a stacked horizontal bar shows the run time with a red portion sized by the failure rate).
    /// </summary>
    public sealed record ProfileDuration(string ProfileName, double AvgSeconds, double SuccessSeconds, double FailureSeconds, int Runs);

    /// <summary>A row in the recent-runs table.</summary>
    public sealed record RecentRun(
        int Id,
        string ProfileName,
        ProfileType Type,
        DateTimeOffset StartedUtc,
        long DurationMs,
        RunOutcome Outcome,
        int Copied,
        int Updated,
        int Deleted,
        int Errors,
        bool Manual,
        int? OperationLogId);
}
