using BackupService.Database;
using BackupService.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Dashboard
{
    /// <summary>
    /// Default <see cref="IDashboardService"/>. Pulls recent <see cref="BackupRun"/> rows via the DbContext
    /// factory and aggregates them in memory. Aggregation is done in memory deliberately: SQLite cannot
    /// <c>ORDER BY</c>/compare a <see cref="DateTimeOffset"/> column, so we order by the monotonic <c>Id</c>
    /// and filter/group by date in C# (the same approach as <c>LogRetentionService</c>).
    /// </summary>
    public sealed class DashboardService(IDatabaseContextFactory contextFactory) : IDashboardService
    {
        /// <summary>Safety cap on how many recent run rows are pulled into memory.</summary>
        private const int MaxRows = 5000;

        /// <summary>Most profiles to show on the per-profile duration chart (keeps it readable).</summary>
        private const int MaxProfilesInChart = 12;

        public async Task<DashboardData> GetAsync(int days, CancellationToken cancellationToken = default)
        {
            if (days < 1)
            {
                days = 1;
            }

            await using var db = contextFactory.CreateDbContext();

            var profiles = await db.Profiles.AsNoTracking()
                .Select(p => p.Enabled)
                .ToListAsync(cancellationToken);
            var totalProfiles = profiles.Count;
            var enabledProfiles = profiles.Count(enabled => enabled);

            // Newest-first by Id (SQLite can't ORDER BY a DateTimeOffset). Project the profile name via the
            // navigation so no Include is needed.
            var recent = await db.BackupRuns.AsNoTracking()
                .OrderByDescending(r => r.Id)
                .Take(MaxRows)
                .Select(r => new RunRow(
                    r.Id,
                    r.Profile != null ? r.Profile.Name : "(deleted profile)",
                    r.Type,
                    r.StartedUtc,
                    r.DurationMs,
                    r.Outcome,
                    r.Copied,
                    r.Updated,
                    r.Deleted,
                    r.Errors,
                    r.Manual,
                    r.OperationLogId))
                .ToListAsync(cancellationToken);

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(days);
            var inPeriod = recent.Where(r => r.StartedUtc >= cutoff).ToList();

            var runsInPeriod = inPeriod.Count;
            var totalSuccess = inPeriod.Count(r => r.Outcome == RunOutcome.Success);
            var totalErrors = inPeriod.Count(r => r.Outcome == RunOutcome.CompletedWithErrors);
            var totalFailed = inPeriod.Count(r => r.Outcome == RunOutcome.Failed);
            var successRate = runsInPeriod == 0 ? 0 : Math.Round(100.0 * totalSuccess / runsInPeriod, 1);
            var filesSynced = inPeriod.Sum(r => (long)(r.Copied + r.Updated));

            return new DashboardData(
                PeriodDays: days,
                TotalProfiles: totalProfiles,
                EnabledProfiles: enabledProfiles,
                DisabledProfiles: totalProfiles - enabledProfiles,
                RunsInPeriod: runsInPeriod,
                SuccessRatePercent: successRate,
                TotalSuccess: totalSuccess,
                TotalCompletedWithErrors: totalErrors,
                TotalFailed: totalFailed,
                FilesSyncedInPeriod: filesSynced,
                LastRunUtc: recent.Count > 0 ? recent[0].StartedUtc : null,
                OutcomesByDay: BuildOutcomesByDay(inPeriod, days),
                DurationByProfile: BuildDurationByProfile(inPeriod),
                RecentRuns: recent.Take(10).Select(ToRecentRun).ToList());
        }

        // A continuous series over the last `days` local-calendar days (zero-filled), oldest → newest.
        private static List<DailyOutcome> BuildOutcomesByDay(List<RunRow> inPeriod, int days)
        {
            var byDay = inPeriod
                .GroupBy(r => DateOnly.FromDateTime(r.StartedUtc.ToLocalTime().Date))
                .ToDictionary(g => g.Key, g => g.ToList());

            var today = DateOnly.FromDateTime(DateTime.Now);
            var result = new List<DailyOutcome>(days);
            for (var day = today.AddDays(-(days - 1)); day <= today; day = day.AddDays(1))
            {
                if (byDay.TryGetValue(day, out var runs))
                {
                    result.Add(new DailyOutcome(
                        day,
                        runs.Count(r => r.Outcome == RunOutcome.Success),
                        runs.Count(r => r.Outcome == RunOutcome.CompletedWithErrors),
                        runs.Count(r => r.Outcome == RunOutcome.Failed)));
                }
                else
                {
                    result.Add(new DailyOutcome(day, 0, 0, 0));
                }
            }

            return result;
        }

        private static List<ProfileDuration> BuildDurationByProfile(List<RunRow> inPeriod) =>
            inPeriod
                .GroupBy(r => r.ProfileName)
                .Select(g =>
                {
                    var avgSeconds = g.Average(r => r.DurationMs) / 1000.0;
                    var successRate = (double)g.Count(r => r.Outcome == RunOutcome.Success) / g.Count();
                    var successSeconds = avgSeconds * successRate;
                    return new ProfileDuration(g.Key, avgSeconds, successSeconds, avgSeconds - successSeconds, g.Count());
                })
                .OrderByDescending(p => p.AvgSeconds)
                .Take(MaxProfilesInChart)
                .ToList();

        private static RecentRun ToRecentRun(RunRow r) => new(
            r.Id, r.ProfileName, r.Type, r.StartedUtc, r.DurationMs, r.Outcome,
            r.Copied, r.Updated, r.Deleted, r.Errors, r.Manual, r.OperationLogId);

        // In-memory projection of a run row (kept minimal for the aggregation above).
        private sealed record RunRow(
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
}
