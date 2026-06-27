using BackupService.Dashboard;
using BackupService.Database;
using BackupService.Enumerations;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BackupService.UnitTests.Dashboard
{
    [TestFixture]
    public class DashboardServiceTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private DashboardService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            _options = new DbContextOptionsBuilder<BackupDbContext>()
                .UseSqlite(_connection)
                .Options;

            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            var factoryMock = new Mock<IDatabaseContextFactory>();
            factoryMock.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            _service = new DashboardService(factoryMock.Object);
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private int SeedProfile(string name, bool enabled)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile { Name = name, Type = ProfileType.FolderPair, Enabled = enabled, DateCreated = DateTimeOffset.UtcNow };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        private void AddRun(int profileId, RunOutcome outcome, long durationMs, DateTimeOffset started,
            int copied = 0, int updated = 0, int deleted = 0, int errors = 0, int warnings = 0,
            long bytesCopied = 0, ProfileType type = ProfileType.FolderPair)
        {
            using var db = new BackupDbContext(_options);
            db.BackupRuns.Add(new BackupRun
            {
                ProfileId = profileId,
                Type = type,
                Outcome = outcome,
                DurationMs = durationMs,
                StartedUtc = started,
                Copied = copied,
                Updated = updated,
                Deleted = deleted,
                Errors = errors,
                Warnings = warnings,
                BytesCopied = bytesCopied,
            });
            db.SaveChanges();
        }

        [Test]
        public async Task GetAsync_AggregatesStatsOutcomesAndDurations()
        {
            var now = DateTimeOffset.UtcNow;
            var profileA = SeedProfile("Alpha", enabled: true);
            var profileB = SeedProfile("Beta", enabled: false);

            // Insert oldest first so Id order matches chronological order (as in production).
            AddRun(profileA, RunOutcome.Success, 2000, now.AddDays(-40), copied: 9, updated: 9); // out of period
            AddRun(profileB, RunOutcome.CompletedWithErrors, 1000, now.AddMinutes(-30), copied: 5, errors: 1);
            AddRun(profileA, RunOutcome.Success, 2000, now.AddMinutes(-20), copied: 3, updated: 1);
            AddRun(profileA, RunOutcome.Failed, 4000, now.AddMinutes(-10), errors: 2);

            var data = await _service.GetAsync(days: 30);

            // Profiles.
            data.TotalProfiles.Should().Be(2);
            data.EnabledProfiles.Should().Be(1);
            data.DisabledProfiles.Should().Be(1);

            // In-period runs (the 40-days-ago run is excluded).
            data.RunsInPeriod.Should().Be(3);
            data.TotalSuccess.Should().Be(1);
            data.TotalCompletedWithErrors.Should().Be(1);
            data.TotalFailed.Should().Be(1);
            data.SuccessRatePercent.Should().BeApproximately(33.3, 0.1);
            data.FilesSyncedInPeriod.Should().Be(9); // (3+1) for A + (5) for B; the failed run synced nothing

            // Outcomes-by-day: one bucket per day, totals match.
            data.OutcomesByDay.Should().HaveCount(30);
            data.OutcomesByDay.Sum(d => d.Success).Should().Be(1);
            data.OutcomesByDay.Sum(d => d.CompletedWithErrors).Should().Be(1);
            data.OutcomesByDay.Sum(d => d.Failed).Should().Be(1);

            // Duration split: A has 2 in-period runs (2s success + 4s failed) → avg 3s, half success / half failure.
            var alpha = data.DurationByProfile.Single(p => p.ProfileName == "Alpha");
            alpha.Runs.Should().Be(2);
            alpha.AvgSeconds.Should().BeApproximately(3.0, 0.001);
            alpha.SuccessSeconds.Should().BeApproximately(1.5, 0.001);
            alpha.FailureSeconds.Should().BeApproximately(1.5, 0.001);

            var beta = data.DurationByProfile.Single(p => p.ProfileName == "Beta");
            beta.AvgSeconds.Should().BeApproximately(1.0, 0.001);
            beta.SuccessSeconds.Should().BeApproximately(0.0, 0.001); // no successes
            beta.FailureSeconds.Should().BeApproximately(1.0, 0.001);

            // Ordered by average duration descending.
            data.DurationByProfile.First().ProfileName.Should().Be("Alpha");

            // Recent runs newest-first by id; the most recent in-period run is the failed one.
            data.RecentRuns.Should().HaveCount(4);
            data.RecentRuns.First().Outcome.Should().Be(RunOutcome.Failed);

            // Last run is the most recently recorded (highest id).
            data.LastRunUtc.Should().NotBeNull();
            data.LastRunUtc!.Value.Should().BeCloseTo(now.AddMinutes(-10), TimeSpan.FromMinutes(1));
        }

        [Test]
        public async Task GetAsync_CountsWarningsSeparately_AndSplitsDuration()
        {
            var now = DateTimeOffset.UtcNow;
            var profile = SeedProfile("Alpha", enabled: true);

            AddRun(profile, RunOutcome.Success, 2000, now.AddMinutes(-30));
            AddRun(profile, RunOutcome.CompletedWithWarnings, 4000, now.AddMinutes(-20), copied: 1, warnings: 3);

            var data = await _service.GetAsync(days: 7);

            data.TotalSuccess.Should().Be(1);
            data.TotalCompletedWithWarnings.Should().Be(1);
            data.TotalCompletedWithErrors.Should().Be(0);
            data.TotalFailed.Should().Be(0);
            data.OutcomesByDay.Sum(d => d.Warnings).Should().Be(1);

            // Duration split: avg 3s, half success / half warning, no failure share.
            var alpha = data.DurationByProfile.Single();
            alpha.AvgSeconds.Should().BeApproximately(3.0, 0.001);
            alpha.SuccessSeconds.Should().BeApproximately(1.5, 0.001);
            alpha.WarningSeconds.Should().BeApproximately(1.5, 0.001);
            alpha.FailureSeconds.Should().BeApproximately(0.0, 0.001);

            data.RecentRuns.First().Warnings.Should().Be(3); // the newest run carried the warnings
        }

        [Test]
        public async Task GetAsync_SplitsArchivesFromFiles_AndSumsBytesPerDay()
        {
            var now = DateTimeOffset.UtcNow;
            var folder = SeedProfile("Folder", enabled: true);
            var archive = SeedProfile("Archive", enabled: true);

            AddRun(folder, RunOutcome.Success, 1000, now.AddMinutes(-30),
                copied: 4, updated: 2, bytesCopied: 2048, type: ProfileType.FolderPair);
            AddRun(archive, RunOutcome.Success, 5000, now.AddMinutes(-20),
                copied: 3, bytesCopied: 1_000_000, type: ProfileType.ArchiveSync);

            var data = await _service.GetAsync(days: 7);

            data.FilesSyncedInPeriod.Should().Be(6);     // 4 copied + 2 updated — folder pair only
            data.ArchivesCreatedInPeriod.Should().Be(3);  // archive 'copied' counted as archives, separately
            data.BytesCopiedInPeriod.Should().Be(2048 + 1_000_000);
            data.BytesByDay.Should().HaveCount(7);
            data.BytesByDay.Sum(b => b.Bytes).Should().Be(2048 + 1_000_000);
        }

        [Test]
        public async Task GetAsync_IncludesScheduledTaskRuns_NamedFromTheTask_AndKindScheduledTask()
        {
            var now = DateTimeOffset.UtcNow;

            int taskId;
            using (var db = new BackupDbContext(_options))
            {
                var task = new ScheduledTask { Name = "Nightly maintenance", DateCreated = now };
                db.ScheduledTasks.Add(task);
                db.SaveChanges();
                taskId = task.Id;

                db.BackupRuns.Add(new BackupRun
                {
                    Kind = RunKind.ScheduledTask,
                    ScheduledTaskId = taskId,
                    Outcome = RunOutcome.Success,
                    DurationMs = 1500,
                    StartedUtc = now.AddMinutes(-15),
                });
                db.SaveChanges();
            }

            var data = await _service.GetAsync(days: 7);

            data.RunsInPeriod.Should().Be(1);
            // No file/byte stats from a task run.
            data.FilesSyncedInPeriod.Should().Be(0);
            data.BytesCopiedInPeriod.Should().Be(0);

            var run = data.RecentRuns.Single();
            run.Kind.Should().Be(RunKind.ScheduledTask);
            run.ProfileName.Should().Be("Nightly maintenance");
        }

        [Test]
        public async Task GetAsync_FiltersByPeriod()
        {
            var now = DateTimeOffset.UtcNow;
            var profile = SeedProfile("Alpha", enabled: true);

            AddRun(profile, RunOutcome.Success, 1000, now.AddDays(-10));
            AddRun(profile, RunOutcome.Success, 1000, now.AddMinutes(-5));

            (await _service.GetAsync(days: 7)).RunsInPeriod.Should().Be(1);   // the 10-day-old run is excluded
            (await _service.GetAsync(days: 14)).RunsInPeriod.Should().Be(2);  // both within 14 days
        }

        [Test]
        public async Task GetAsync_WhenNoRuns_ReturnsZeroedStats()
        {
            SeedProfile("Alpha", enabled: true);

            var data = await _service.GetAsync(days: 14);

            data.RunsInPeriod.Should().Be(0);
            data.SuccessRatePercent.Should().Be(0);
            data.DurationByProfile.Should().BeEmpty();
            data.RecentRuns.Should().BeEmpty();
            data.OutcomesByDay.Should().HaveCount(14);
            data.LastRunUtc.Should().BeNull();
        }
    }
}
