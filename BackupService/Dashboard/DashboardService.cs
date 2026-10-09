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
        /// <summary>How many run rows are read per round trip while paging back to the start of the period.</summary>
        private const int PageRows = 5000;

        /// <summary>
        /// A safety cap on the rows read in all. Far above a real period: a busy watcher flush records a run each
        /// time, so a single page (the old cap) could cover only part of a 30-day view.
        /// </summary>
        private const int MaxRows = 200_000;

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

            // The period is whole local days — the same window the charts draw — so the cards add up to the charts.
            var periodStart = new DateTimeOffset(DateTime.Today.AddDays(-(days - 1)));
            var recent = await ReadRunsSinceAsync(db, periodStart, cancellationToken);
            var inPeriod = recent.Where(r => r.StartedUtc >= periodStart).ToList();

            var runsInPeriod = inPeriod.Count;
            var totalSuccess = inPeriod.Count(r => r.Outcome == RunOutcome.Success);
            var totalWarnings = inPeriod.Count(r => r.Outcome == RunOutcome.CompletedWithWarnings);
            var totalErrors = inPeriod.Count(r => r.Outcome == RunOutcome.CompletedWithErrors);
            var totalFailed = inPeriod.Count(r => r.Outcome == RunOutcome.Failed);
            var successRate = runsInPeriod == 0 ? 0 : Math.Round(100.0 * totalSuccess / runsInPeriod, 1);
            // Regular file copies/updates (OneWaySyncItem + InstantSync) are counted separately from archives
            // (ArchiveSync zips), which the dashboard shows on their own.
            var filesSynced = inPeriod.Where(r => r.Type != ProfileType.ArchiveSync).Sum(r => (long)(r.Copied + r.Updated));
            var archivesCreated = inPeriod.Where(r => r.Type == ProfileType.ArchiveSync).Sum(r => r.Copied);
            var bytesCopied = inPeriod.Sum(r => r.BytesCopied);

            return new DashboardData(
                PeriodDays: days,
                TotalProfiles: totalProfiles,
                EnabledProfiles: enabledProfiles,
                DisabledProfiles: totalProfiles - enabledProfiles,
                RunsInPeriod: runsInPeriod,
                SuccessRatePercent: successRate,
                TotalSuccess: totalSuccess,
                TotalCompletedWithWarnings: totalWarnings,
                TotalCompletedWithErrors: totalErrors,
                TotalFailed: totalFailed,
                FilesSyncedInPeriod: filesSynced,
                ArchivesCreatedInPeriod: archivesCreated,
                BytesCopiedInPeriod: bytesCopied,
                LastRunUtc: recent.Count > 0 ? recent[0].StartedUtc : null,
                OutcomesByDay: BuildOutcomesByDay(inPeriod, days),
                BytesByDay: BuildBytesByDay(inPeriod, days),
                DurationByProfile: BuildDurationByProfile(inPeriod),
                RecentRuns: recent.Take(10).Select(ToRecentRun).ToList());
        }

        // Newest-first by Id (SQLite can't ORDER BY or compare a DateTimeOffset), a page at a time, until the rows
        // reach back past the period start. A row is written when its run finishes, so Ids follow finish times:
        // once a row finished before the period started, every older row did too (and so started before it) — a
        // row that merely *started* earlier may be a long run, with shorter in-period runs written before it.
        // Projects the profile/task name via the navigation so no Include is needed.
        private static async Task<List<RunRow>> ReadRunsSinceAsync(BackupDbContext db, DateTimeOffset periodStart, CancellationToken cancellationToken)
        {
            var rows = new List<RunRow>();
            var beforeId = int.MaxValue;
            while (rows.Count < MaxRows)
            {
                var below = beforeId;
                var page = await db.BackupRuns.AsNoTracking()
                    .Where(r => r.Id < below)
                    .OrderByDescending(r => r.Id)
                    .Take(PageRows)
                    .Select(r => new RunRow(
                        r.Id,
                        r.Kind,
                        r.Kind == RunKind.ScheduledTask
                            ? (r.ScheduledTask != null ? r.ScheduledTask.Name : "(deleted task)")
                            : (r.Profile != null ? r.Profile.Name : "(deleted profile)"),
                        r.Type,
                        r.StartedUtc,
                        r.DurationMs,
                        r.Outcome,
                        r.Copied,
                        r.Updated,
                        r.Deleted,
                        r.Errors,
                        r.Warnings,
                        r.BytesCopied,
                        r.Manual,
                        r.OperationLogId))
                    .ToListAsync(cancellationToken);

                rows.AddRange(page);
                if (page.Count < PageRows
                    || page.Any(r => r.StartedUtc.AddMilliseconds(r.DurationMs) < periodStart))
                {
                    break;
                }
                beforeId = page[^1].Id;
            }

            return rows;
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
                        runs.Count(r => r.Outcome == RunOutcome.CompletedWithWarnings),
                        runs.Count(r => r.Outcome == RunOutcome.CompletedWithErrors),
                        runs.Count(r => r.Outcome == RunOutcome.Failed)));
                }
                else
                {
                    result.Add(new DailyOutcome(day, 0, 0, 0, 0));
                }
            }

            return result;
        }

        // A continuous series over the last `days` local-calendar days (zero-filled), oldest → newest.
        private static List<DailyBytes> BuildBytesByDay(List<RunRow> inPeriod, int days)
        {
            var byDay = inPeriod
                .GroupBy(r => DateOnly.FromDateTime(r.StartedUtc.ToLocalTime().Date))
                .ToDictionary(g => g.Key, g => g.Sum(r => r.BytesCopied));

            var today = DateOnly.FromDateTime(DateTime.Now);
            var result = new List<DailyBytes>(days);
            for (var day = today.AddDays(-(days - 1)); day <= today; day = day.AddDays(1))
            {
                result.Add(new DailyBytes(day, byDay.GetValueOrDefault(day)));
            }

            return result;
        }

        private static List<ProfileDuration> BuildDurationByProfile(List<RunRow> inPeriod) =>
            inPeriod
                .GroupBy(r => r.ProfileName)
                .Select(g =>
                {
                    var total = g.Count();
                    var avgSeconds = g.Average(r => r.DurationMs) / 1000.0;
                    var successSeconds = avgSeconds * g.Count(r => r.Outcome == RunOutcome.Success) / total;
                    var warningSeconds = avgSeconds * g.Count(r => r.Outcome == RunOutcome.CompletedWithWarnings) / total;
                    // Whatever's left is the failure share (completed-with-errors + failed).
                    var failureSeconds = avgSeconds - successSeconds - warningSeconds;
                    return new ProfileDuration(g.Key, avgSeconds, successSeconds, warningSeconds, failureSeconds, total);
                })
                .OrderByDescending(p => p.AvgSeconds)
                .Take(MaxProfilesInChart)
                .ToList();

        private static RecentRun ToRecentRun(RunRow r) => new(
            r.Id, r.Kind, r.ProfileName, r.Type, r.StartedUtc, r.DurationMs, r.Outcome,
            r.Copied, r.Updated, r.Deleted, r.Errors, r.Warnings, r.Manual, r.OperationLogId);

        // In-memory projection of a run row (kept minimal for the aggregation above).
        private sealed record RunRow(
            int Id,
            RunKind Kind,
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
            long BytesCopied,
            bool Manual,
            int? OperationLogId);
    }
}
