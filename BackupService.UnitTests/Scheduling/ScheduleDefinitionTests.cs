using BackupService.Enumerations;
using BackupService.Scheduling;
using Cronos;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class ScheduleDefinitionTests
    {
        [Test]
        public void EveryNMinutes_ProducesCronAndLabel()
        {
            var def = new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = 15 };

            def.ToCron().Should().Be("*/15 * * * *");
            def.ToHumanReadable().Should().Be("Every 15 minutes");
        }

        [Test]
        public void Hourly_ProducesCronAndLabel()
        {
            var def = new ScheduleDefinition { Mode = ScheduleMode.Hourly, Minute = 30 };

            def.ToCron().Should().Be("30 * * * *");
            def.ToHumanReadable().Should().Be("Every hour at minute 30");
        }

        [Test]
        public void Daily_ProducesCronAndLabel()
        {
            var def = new ScheduleDefinition { Mode = ScheduleMode.Daily, Hour = 2, Minute = 0 };

            def.ToCron().Should().Be("0 2 * * *");
            def.ToHumanReadable().Should().Be("Every day at 02:00 AM");
        }

        [Test]
        public void Weekly_ProducesCronAndLabel()
        {
            var def = new ScheduleDefinition
            {
                Mode = ScheduleMode.Weekly,
                Hour = 3,
                Minute = 0,
                DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
            };

            def.ToCron().Should().Be("0 3 * * 1,3,5");
            def.ToHumanReadable().Should().Be("Every Mon, Wed, Fri at 03:00 AM");
        }

        [Test]
        public void Monthly_ProducesCronAndLabel()
        {
            var def = new ScheduleDefinition { Mode = ScheduleMode.Monthly, DayOfMonth = 15, Hour = 4, Minute = 0 };

            def.ToCron().Should().Be("0 4 15 * *");
            def.ToHumanReadable().Should().Be("On day 15 of every month at 04:00 AM");
        }

        [TestCase(ScheduleMode.EveryNMinutes)]
        [TestCase(ScheduleMode.Hourly)]
        [TestCase(ScheduleMode.Daily)]
        [TestCase(ScheduleMode.Weekly)]
        [TestCase(ScheduleMode.Monthly)]
        public void EveryMode_ProducesCronThatCronosCanParse(ScheduleMode mode)
        {
            var def = new ScheduleDefinition { Mode = mode };

            var parse = () => CronExpression.Parse(def.ToCron());

            parse.Should().NotThrow();
        }

        [Test]
        public void FromCron_RoundTripsEachMode()
        {
            var definitions = new[]
            {
                new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = 20 },
                new ScheduleDefinition { Mode = ScheduleMode.Hourly, Minute = 5 },
                new ScheduleDefinition { Mode = ScheduleMode.Daily, Hour = 6, Minute = 30 },
                new ScheduleDefinition { Mode = ScheduleMode.Weekly, Hour = 7, Minute = 15, DaysOfWeek = [DayOfWeek.Tuesday, DayOfWeek.Saturday] },
                new ScheduleDefinition { Mode = ScheduleMode.Monthly, DayOfMonth = 12, Hour = 8, Minute = 0 },
            };

            foreach (var def in definitions)
            {
                var parsed = ScheduleDefinition.FromCron(def.ToCron());

                parsed.Should().NotBeNull();
                parsed!.ToCron().Should().Be(def.ToCron());
                parsed.ToHumanReadable().Should().Be(def.ToHumanReadable());
            }
        }

        [TestCase(null, "Not scheduled")]
        [TestCase("", "Not scheduled")]
        [TestCase("0 2 * * *", "Every day at 02:00 AM")]
        [TestCase("*/15 * * * *", "Every 15 minutes")]
        public void Describe_GivesHumanReadableOrNotScheduled(string? cron, string expected)
        {
            ScheduleDefinition.Describe(cron).Should().Be(expected);
        }

        [Test]
        public void FromCron_ReturnsNullForUnrecognisedForm()
        {
            ScheduleDefinition.FromCron("not a cron").Should().BeNull();
        }

        [TestCase(ScheduleMode.EveryNMinutes)]
        [TestCase(ScheduleMode.Hourly)]
        [TestCase(ScheduleMode.Daily)]
        [TestCase(ScheduleMode.Weekly)]
        [TestCase(ScheduleMode.Monthly)]
        public void TheDefaultForEveryMode_IsValid(ScheduleMode mode)
        {
            new ScheduleDefinition { Mode = mode }.GetValidationError().Should().BeNull();
        }

        [Test]
        public void OutOfRangeValues_AreRefused_SoNothingThatCantRunIsSaved()
        {
            // Each of these used to be saved, after which the scheduler dropped it without a word.
            new ScheduleDefinition { Mode = ScheduleMode.Hourly, Minute = 75 }.GetValidationError().Should().NotBeNull();
            new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = 0 }.GetValidationError().Should().NotBeNull();
            new ScheduleDefinition { Mode = ScheduleMode.Daily, Hour = 24 }.GetValidationError().Should().NotBeNull();
            new ScheduleDefinition { Mode = ScheduleMode.Monthly, DayOfMonth = 0 }.GetValidationError().Should().NotBeNull();
            new ScheduleDefinition { Mode = ScheduleMode.Monthly, DayOfMonth = 32 }.GetValidationError().Should().NotBeNull();
            new ScheduleDefinition { Mode = ScheduleMode.Weekly, DaysOfWeek = [] }.GetValidationError().Should().NotBeNull();
        }

        [Test]
        public void AnIntervalThatDoesNotDivideTheHour_IsRefused_AndDescribedAsWhatItReallyDoes()
        {
            // "*/45" restarts every hour: it runs at :00 and :45, not every 45 minutes.
            var def = new ScheduleDefinition { Mode = ScheduleMode.EveryNMinutes, IntervalMinutes = 45 };

            def.GetValidationError().Should().NotBeNull();
            ScheduleDefinition.Describe("*/45 * * * *").Should().Be("Every hour at minutes 00, 45");
        }

        [Test]
        public void EveryIntervalOffered_DividesTheHour()
        {
            ScheduleDefinition.EvenIntervals.Should().OnlyContain(n => 60 % n == 0);
        }

        [Test]
        public void LastDayOfTheMonth_RunsAtTheEndOfEveryMonth_AndRoundTrips()
        {
            var def = new ScheduleDefinition { Mode = ScheduleMode.Monthly, LastDayOfMonth = true, Hour = 23, Minute = 0 };

            def.ToCron().Should().Be("0 23 L * *");
            def.GetValidationError().Should().BeNull();
            def.ToHumanReadable().Should().Be("On the last day of every month at 11:00 PM");
            ScheduleDefinition.FromCron(def.ToCron())!.LastDayOfMonth.Should().BeTrue();

            var next = CronExpression.Parse(def.ToCron()).GetNextOccurrence(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            next.Should().Be(new DateTime(2026, 2, 28, 23, 0, 0, DateTimeKind.Utc));
        }

        [Test]
        public void ADayThatSomeMonthsLack_SaysThoseMonthsAreSkipped()
        {
            new ScheduleDefinition { Mode = ScheduleMode.Monthly, DayOfMonth = 31, Hour = 4 }.ToHumanReadable()
                .Should().Be("On day 31 of each month that has one at 04:00 AM");
        }

        [TestCase("75 * * * *")]
        [TestCase("*/0 * * * *")]
        [TestCase("0 2 32 * *")]
        public void Describe_AStoredCronThatCantRun_SaysSo(string cron)
        {
            ScheduleDefinition.Describe(cron).Should().Be("Invalid schedule — not running");
        }

        [TestCase("75 * * * *")]
        [TestCase("75 2 * * *")]
        [TestCase("0 25 * * *")]
        [TestCase("0 2 0 * *")]
        public void FromCron_OutOfRangeValues_ReturnNull_RatherThanADefinitionThatCrashesWhenShown(string cron)
        {
            ScheduleDefinition.FromCron(cron).Should().BeNull();
        }
    }
}
