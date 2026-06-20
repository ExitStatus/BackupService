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
        int TotalCompletedWithWarnings,
        int TotalCompletedWithErrors,
        int TotalFailed,
        long FilesSyncedInPeriod,
        int ArchivesCreatedInPeriod,
        long BytesCopiedInPeriod,
        DateTimeOffset? LastRunUtc,
        IReadOnlyList<DailyOutcome> OutcomesByDay,
        IReadOnlyList<DailyBytes> BytesByDay,
        IReadOnlyList<ProfileDuration> DurationByProfile,
        IReadOnlyList<RecentRun> RecentRuns)
    {
        public static DashboardData Empty(int periodDays) =>
            new(periodDays, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, [], [], [], []);
    }

    /// <summary>One day's run-outcome tally (for the stacked-column "runs over time" chart).</summary>
    public sealed record DailyOutcome(DateOnly Date, int Success, int Warnings, int CompletedWithErrors, int Failed);

    /// <summary>One day's total bytes copied (for the data-volume bar chart).</summary>
    public sealed record DailyBytes(DateOnly Date, long Bytes);

    /// <summary>
    /// A profile's average run time split into success-, warning- and failure-weighted portions
    /// (so a stacked horizontal bar shows the run time with an amber warning portion and a red failure
    /// portion, each sized by the corresponding rate).
    /// </summary>
    public sealed record ProfileDuration(string ProfileName, double AvgSeconds, double SuccessSeconds, double WarningSeconds, double FailureSeconds, int Runs);

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
        int Warnings,
        bool Manual,
        int? OperationLogId);
}
