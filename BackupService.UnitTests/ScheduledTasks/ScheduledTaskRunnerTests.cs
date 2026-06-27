using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.ScheduledTasks;
using BackupService.Scheduling;
using BackupService.Scheduling.ScheduledTasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.ScheduledTasks
{
    [TestFixture]
    public class ScheduledTaskRunnerTests
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private ScheduledTaskStatusService _statusService = null!;

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
            _dbFactory = factoryMock.Object;
            _statusService = new ScheduledTaskStatusService();
        }

        [TearDown]
        public void TearDown() => _connection.Dispose();

        private async Task<int> SeedTaskAsync(params (string Command, bool Shell)[] steps)
        {
            await using var db = new BackupDbContext(_options);
            var task = new ScheduledTask
            {
                Name = "Nightly",
                DateCreated = DateTimeOffset.UtcNow,
            };
            for (var i = 0; i < steps.Length; i++)
            {
                task.Steps.Add(new ScheduledTaskStep
                {
                    Order = i,
                    Name = $"S{i + 1}",
                    Command = steps[i].Command,
                    RunViaShell = steps[i].Shell,
                });
            }
            db.ScheduledTasks.Add(task);
            await db.SaveChangesAsync();
            return task.Id;
        }

        private ScheduledTaskRunner Runner(IProcessRunner processRunner, IBackupRunRecorder recorder) =>
            new(_dbFactory, new OperationLogFactory(_dbFactory), _statusService, processRunner, recorder, NullLogger<ScheduledTaskRunner>.Instance);

        [Test]
        public async Task RunAsync_RunsStepsInOrder_RecordsSuccess()
        {
            var taskId = await SeedTaskAsync(("one", false), ("two", false));
            var processor = new FakeProcessRunner();
            var recorder = new FakeRecorder();

            await Runner(processor, recorder).RunAsync(taskId, manual: false, CancellationToken.None);

            processor.RanCommands.Should().Equal("one", "two");
            recorder.Calls.Should().ContainSingle();
            recorder.Calls[0].Outcome.Should().Be(RunOutcome.Success);
            recorder.Calls[0].ScheduledTaskId.Should().Be(taskId);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Scheduled Task 'Nightly' ran successfully in");
            log.Level.Should().Be(OperationLogLevel.Info);
            _statusService.Get(taskId).Should().Be(ProfileStatus.Idle);
        }

        [Test]
        public async Task RunAsync_WhenManual_PrefixesLogWithManualTag()
        {
            var taskId = await SeedTaskAsync(("one", false));

            await Runner(new FakeProcessRunner(), new FakeRecorder()).RunAsync(taskId, manual: true, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("[Manual] Scheduled Task 'Nightly' ran successfully in");
        }

        [Test]
        public async Task RunAsync_CapturesStdoutAndStderr_InLogDetail()
        {
            var taskId = await SeedTaskAsync(("one", false));
            var processor = new FakeProcessRunner
            {
                OnRun = _ => new ProcessRunResult(0, ["hello out"], ["a warning on stderr"]),
            };

            await Runner(processor, new FakeRecorder()).RunAsync(taskId, manual: false, CancellationToken.None);

            await using var verify = new BackupDbContext(_options);
            var details = await verify.OperationLogDetails.OrderBy(d => d.Sequence).ToListAsync();
            details.Should().Contain(d => d.Message.Contains("hello out") && d.Level == OperationLogLevel.Info);
            details.Should().Contain(d => d.Message.Contains("a warning on stderr") && d.Level == OperationLogLevel.Error);
        }

        [Test]
        public async Task RunAsync_StopsAtFirstFailingStep_MarksFailed_AndSetsError()
        {
            var taskId = await SeedTaskAsync(("ok", false), ("fail", false), ("never", false));
            var processor = new FakeProcessRunner
            {
                OnRun = step => step.Command == "fail"
                    ? new ProcessRunResult(1, [], ["boom"])
                    : new ProcessRunResult(0, [], []),
            };
            var recorder = new FakeRecorder();

            await Runner(processor, recorder).RunAsync(taskId, manual: false, CancellationToken.None);

            // The third step must not run.
            processor.RanCommands.Should().Equal("ok", "fail");
            recorder.Calls.Single().Outcome.Should().Be(RunOutcome.Failed);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Scheduled Task 'Nightly' failed at");
            log.Level.Should().Be(OperationLogLevel.Error);
            _statusService.Get(taskId).Should().Be(ProfileStatus.Error);
        }

        [Test]
        public async Task RunAsync_WhenCancelled_WritesWarning_AndReturnsToIdle()
        {
            var taskId = await SeedTaskAsync(("one", false), ("two", false));
            using var cts = new CancellationTokenSource();
            var processor = new FakeProcessRunner
            {
                OnRun = _ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException();
                },
            };
            var recorder = new FakeRecorder();

            // The runner handles cancellation internally — it should not throw out.
            await Runner(processor, recorder).RunAsync(taskId, manual: false, cts.Token);

            recorder.Calls.Single().Outcome.Should().Be(RunOutcome.CompletedWithWarnings);

            await using var verify = new BackupDbContext(_options);
            var log = await verify.OperationLogs.SingleAsync();
            log.Name.Should().StartWith("Scheduled Task 'Nightly' was cancelled after");
            log.Level.Should().Be(OperationLogLevel.Warning);

            // A cancelled run is a warning, not an error.
            _statusService.Get(taskId).Should().NotBe(ProfileStatus.Error);
        }

        private sealed class FakeProcessRunner : IProcessRunner
        {
            public List<string> RanCommands { get; } = [];

            public Func<ScheduledTaskStep, ProcessRunResult>? OnRun { get; init; }

            public Task<ProcessRunResult> RunAsync(ScheduledTaskStep step, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RanCommands.Add(step.Command ?? string.Empty);
                var result = OnRun?.Invoke(step) ?? new ProcessRunResult(0, [], []);
                return Task.FromResult(result);
            }
        }

        private sealed class FakeRecorder : IBackupRunRecorder
        {
            public List<(int ScheduledTaskId, bool Manual, RunOutcome Outcome, int? OperationLogId)> Calls { get; } = [];

            public Task RecordAsync(int profileId, ProfileType type, bool manual, DateTimeOffset startedUtc, double durationMs, BackupResult counts, RunOutcome outcome, int? operationLogId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task RecordScheduledTaskAsync(int scheduledTaskId, bool manual, DateTimeOffset startedUtc, double durationMs, RunOutcome outcome, int? operationLogId, CancellationToken cancellationToken = default)
            {
                Calls.Add((scheduledTaskId, manual, outcome, operationLogId));
                return Task.CompletedTask;
            }
        }
    }
}
