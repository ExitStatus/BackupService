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
    }
}
