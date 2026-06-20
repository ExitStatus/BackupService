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
            int copied = 0, int updated = 0, int deleted = 0, int errors = 0)
        {
            using var db = new BackupDbContext(_options);
            db.BackupRuns.Add(new BackupRun
            {
                ProfileId = profileId,
                Type = ProfileType.FolderPair,
                Outcome = outcome,
                DurationMs = durationMs,
                StartedUtc = started,
                Copied = copied,
                Updated = updated,
                Deleted = deleted,
                Errors = errors,
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
