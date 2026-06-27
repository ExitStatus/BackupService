using BackupService.Database;
using BackupService.Enumerations;
using BackupService.ScheduledTasks;
using FluentAssertions;

namespace BackupService.UnitTests.ScheduledTasks
{
    [TestFixture]
    public class ScheduledTaskStepServiceTests
    {
        private readonly ScheduledTaskStepService _sut = new();

        [Test]
        public void Add_BuildsStepsFromInputs()
        {
            var task = new ScheduledTask { Name = "T" };

            _sut.Add(task, [
                new ScheduledTaskStepInput(0, 0, "First", "a.exe", "--x", @"C:\w", false),
                new ScheduledTaskStepInput(0, 1, null, "echo hi", null, null, true),
            ]);

            task.Steps.Should().HaveCount(2);
            task.Steps.Should().Contain(s => s.Order == 0 && s.Name == "First" && s.Command == "a.exe" && s.Arguments == "--x" && s.WorkingDirectory == @"C:\w" && !s.RunViaShell);
            task.Steps.Should().Contain(s => s.Order == 1 && s.Command == "echo hi" && s.RunViaShell);
        }

        [Test]
        public void Sync_UpdatesMatchedById_AddsId0_RemovesTheRest()
        {
            var task = new ScheduledTask
            {
                Name = "T",
                Steps =
                {
                    new ScheduledTaskStep { Id = 1, Order = 0, Name = "Keep", Command = "a", RunViaShell = false },
                    new ScheduledTaskStep { Id = 2, Order = 1, Name = "Drop", Command = "b", RunViaShell = false },
                },
            };

            var changes = _sut.Sync(task, [
                new ScheduledTaskStepInput(1, 0, "Renamed", "a2", null, null, true), // update matched
                new ScheduledTaskStepInput(0, 1, "New", "c", null, null, false),     // add new (id 0)
                // id 2 omitted → removed
            ]);

            task.Steps.Should().HaveCount(2);
            task.Steps.Should().Contain(s => s.Id == 1 && s.Command == "a2" && s.Name == "Renamed" && s.RunViaShell);
            task.Steps.Should().Contain(s => s.Id == 0 && s.Command == "c" && s.Name == "New");
            task.Steps.Should().NotContain(s => s.Id == 2);
            changes.Should().NotBeEmpty();
        }

        [Test]
        public void Sync_WhenNothingChanged_ReportsNoChanges()
        {
            var task = new ScheduledTask
            {
                Name = "T",
                Steps = { new ScheduledTaskStep { Id = 1, Order = 0, Name = "Same", Command = "a", Arguments = "x", RunViaShell = false } },
            };

            var changes = _sut.Sync(task, [
                new ScheduledTaskStepInput(1, 0, "Same", "a", "x", null, false),
            ]);

            changes.Should().BeEmpty();
        }

        [Test]
        public void Add_PowerShellStep_PersistsKindAndScript()
        {
            var task = new ScheduledTask { Name = "T" };

            _sut.Add(task, [
                new ScheduledTaskStepInput(0, 0, "Cleanup", null, null, null, false,
                    ScheduledTaskStepKind.PowerShell, "Get-ChildItem | Remove-Item"),
            ]);

            var step = task.Steps.Single();
            step.Kind.Should().Be(ScheduledTaskStepKind.PowerShell);
            step.Script.Should().Be("Get-ChildItem | Remove-Item");
            step.Command.Should().BeNull();
        }

        [Test]
        public void DescribeForCreateLog_DescribesPowerShellStep()
        {
            var lines = _sut.DescribeForCreateLog([
                new ScheduledTaskStepInput(0, 0, "Cleanup", null, null, null, false,
                    ScheduledTaskStepKind.PowerShell, "Write-Host hi"),
            ]);

            lines.Single().Should().Contain("Cleanup").And.Contain("PowerShell script");
        }

        [Test]
        public void DescribeForCreateLog_OrdersByStepOrder()
        {
            var lines = _sut.DescribeForCreateLog([
                new ScheduledTaskStepInput(0, 1, "Second", "b", null, null, false),
                new ScheduledTaskStepInput(0, 0, "First", "a", null, null, false),
            ]);

            lines.Should().HaveCount(2);
            lines[0].Should().StartWith("Step 1:").And.Contain("First");
            lines[1].Should().StartWith("Step 2:").And.Contain("Second");
        }
    }
}
