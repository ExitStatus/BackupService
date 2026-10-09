using System.Collections.Concurrent;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using BackupService.Scheduling;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BackupService.UnitTests.Scheduling
{
    /// <summary>
    /// Drives the watcher with real folders and a real <see cref="FileSystemWatcher"/>, against a recording fake
    /// processor: that nothing it reports is lost (failed flushes retried, uncopied files caught up, a missing source
    /// folder watched once it appears) and that a pass never outlives or overlaps its item's settings.
    /// </summary>
    [TestFixture]
    public class InstantSyncWatcherServiceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private IDatabaseContextFactory _dbFactory = null!;
        private BackupService.UnitTests.Logging.TempLogStore _logStore = null!;
        private FakeProcessor _processor = null!;
        private Mock<IOneWaySyncSynchronizer> _synchronizer = null!;
        private ConcurrentQueue<OneWaySyncItem> _reconciles = null!;
        private ConcurrentQueue<RunOutcome> _recordedRuns = null!;
        private InstantSyncWatcherService _sut = null!;
        private string _root = null!;
        private string _source = null!;

        [SetUp]
        public void SetUp()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<BackupDbContext>().UseSqlite(_connection).Options;
            using (var context = new BackupDbContext(_options))
            {
                context.Database.EnsureCreated();
            }

            var factory = new Mock<IDatabaseContextFactory>();
            factory.Setup(f => f.CreateDbContext()).Returns(() => new BackupDbContext(_options));
            _dbFactory = factory.Object;
            _logStore = new BackupService.UnitTests.Logging.TempLogStore();

            _processor = new FakeProcessor();
            _reconciles = new ConcurrentQueue<OneWaySyncItem>();
            _synchronizer = new Mock<IOneWaySyncSynchronizer>();
            _synchronizer
                .Setup(s => s.SyncAsync(It.IsAny<OneWaySyncItem>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<IOperationLogger>(),
                    It.IsAny<CancellationToken>(), It.IsAny<IProgress<int>?>(), It.IsAny<Action<string?>?>()))
                .Callback((OneWaySyncItem pair, int? _, int? _, IOperationLogger _, CancellationToken _, IProgress<int>? _, Action<string?>? _) => _reconciles.Enqueue(pair))
                .ReturnsAsync(new BackupResult());

            _recordedRuns = new ConcurrentQueue<RunOutcome>();
            var runRecorder = new Mock<IBackupRunRecorder>();
            runRecorder
                .Setup(r => r.RecordAsync(It.IsAny<int>(), It.IsAny<ProfileType>(), It.IsAny<bool>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<double>(), It.IsAny<BackupResult>(), It.IsAny<RunOutcome>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Callback((int _, ProfileType _, bool _, DateTimeOffset _, double _, BackupResult _, RunOutcome outcome, int? _, CancellationToken _) => _recordedRuns.Enqueue(outcome))
                .Returns(Task.CompletedTask);

            _sut = new InstantSyncWatcherService(_dbFactory, _processor, _synchronizer.Object,
                new OperationLogFactory(_dbFactory, _logStore.Store), runRecorder.Object,
                NullLogger<InstantSyncWatcherService>.Instance)
            {
                RetryBaseDelay = TimeSpan.FromMilliseconds(100),
                WatchRetryInterval = TimeSpan.FromMilliseconds(100),
            };

            _root = Path.Combine(Path.GetTempPath(), "bs-instantwatch-" + Guid.NewGuid().ToString("N"));
            _source = Path.Combine(_root, "source");
            Directory.CreateDirectory(_source);
        }

        [TearDown]
        public void TearDown()
        {
            _sut.Dispose();
            _connection.Dispose();
            _logStore.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private int SeedProfile(bool enabled = true)
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = "Live",
                Type = ProfileType.InstantSync,
                Enabled = enabled,
                DateCreated = DateTimeOffset.UtcNow,
                InstantSyncItems =
                {
                    new InstantSyncItem
                    {
                        Name = "Docs",
                        SourceFolder = _source,
                        TargetFolder = Path.Combine(_root, "target"),
                        DebounceMilliseconds = 50,
                        IncludeSubFolders = true,
                        AllowDeletions = true,
                    },
                },
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        private void SetEnabled(int profileId, bool enabled)
        {
            using var db = new BackupDbContext(_options);
            db.Profiles.Single(p => p.Id == profileId).Enabled = enabled;
            db.SaveChanges();
        }

        private string WriteSourceFile(string name)
        {
            var path = Path.Combine(_source, name);
            File.WriteAllText(path, "content");
            return path;
        }

        [Test]
        public async Task AChangedFile_IsFlushedAfterTheDebounce()
        {
            await _sut.SyncAsync(SeedProfile());

            var file = WriteSourceFile("a.txt");

            await WaitUntilAsync(() => _processor.Calls.Any(c => c.Changes.Contains(file)));
        }

        [Test]
        public async Task AFlushThatFails_IsRetried_WithTheSameChanges()
        {
            // e.g. the target drive was briefly unavailable: the changes must not be dropped.
            _processor.Behaviour = (call, _) => call == 1
                ? throw new IOException("The target is unavailable.")
                : Task.FromResult(new BackupResult());
            await _sut.SyncAsync(SeedProfile());

            var file = WriteSourceFile("a.txt");

            await WaitUntilAsync(() => _processor.Calls.Count >= 2);
            _processor.Calls.ElementAt(1).Changes.Should().Contain(file);
        }

        [Test]
        public async Task ATargetThatStaysDown_IsLoggedAsOneFailure_NotOnePerRetry()
        {
            // A NAS offline for a day would otherwise add an Error log and a Failed run every 30 minutes.
            _processor.Behaviour = (call, _) => call <= 3
                ? throw new IOException("The target is unavailable.")
                : Task.FromResult(new BackupResult { Copied = 1 });
            await _sut.SyncAsync(SeedProfile());

            WriteSourceFile("a.txt");

            await WaitUntilAsync(() => _processor.Calls.Count >= 4 && _recordedRuns.Count >= 2);
            _recordedRuns.Should().Equal(RunOutcome.Failed, RunOutcome.Success);
            using var db = new BackupDbContext(_options);
            db.OperationLogs.Count(l => l.Level == OperationLogLevel.Error).Should().Be(1);
        }

        [Test]
        public async Task AFlushThatCouldNotCopySomeFiles_IsFollowedByACatchUpPass()
        {
            _processor.Behaviour = (call, _) => Task.FromResult(call == 1 ? new BackupResult { Warnings = 1 } : new BackupResult());
            await _sut.SyncAsync(SeedProfile());

            WriteSourceFile("locked.txt");

            await WaitUntilAsync(() => !_reconciles.IsEmpty);
            var pass = _reconciles.First();
            pass.SourceFolder.Should().Be(_source);
            pass.OverwriteBehaviour.Should().Be(OverwriteBehaviour.AlwaysOverwrite, "instant sync is source-authoritative");
        }

        [Test]
        public async Task AnItemWhoseSourceFolderIsMissing_IsWatchedOnceItAppears_AndCaughtUp()
        {
            // A drive not yet mounted at logon: the item must start being watched when it is, and catch up on what
            // was written meanwhile.
            Directory.Delete(_source);
            var profileId = SeedProfile();
            await _sut.SyncAsync(profileId);
            _sut.WatcherCount(profileId).Should().Be(0);

            Directory.CreateDirectory(_source);

            await WaitUntilAsync(() => _sut.WatcherCount(profileId) == 1);
            await WaitUntilAsync(() => !_reconciles.IsEmpty);
        }

        [Test]
        public async Task DisablingTheProfile_StopsARunningPass()
        {
            var cancelled = new TaskCompletionSource();
            _processor.Behaviour = async (_, token) =>
            {
                await using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return new BackupResult();
            };
            var profileId = SeedProfile();
            await _sut.SyncAsync(profileId);
            WriteSourceFile("a.txt");
            await WaitUntilAsync(() => !_processor.Calls.IsEmpty);

            SetEnabled(profileId, false);
            await _sut.SyncAsync(profileId);

            await cancelled.Task.WaitAsync(Timeout);
            _sut.WatcherCount(profileId).Should().Be(0);
        }

        [Test]
        public async Task AnEditWhileAPassRuns_DoesNotStartASecondPassForTheItemUntilTheFirstEnds()
        {
            // The replaced watcher's pass may still be unwinding (here it ignores the cancellation); two passes
            // must never write the same target at once.
            var release = new TaskCompletionSource();
            _processor.Behaviour = async (call, _) =>
            {
                if (call == 1)
                {
                    await release.Task;
                }
                return new BackupResult();
            };
            var profileId = SeedProfile();
            await _sut.SyncAsync(profileId);
            WriteSourceFile("a.txt");
            await WaitUntilAsync(() => !_processor.Calls.IsEmpty);

            await _sut.SyncAsync(profileId); // an edit: the item's watcher is replaced
            var second = WriteSourceFile("b.txt");
            await Task.Delay(500);
            _processor.Calls.Should().HaveCount(1, "the new watcher's pass waits for the old one");

            release.SetResult();
            await WaitUntilAsync(() => _processor.Calls.Any(c => c.Changes.Contains(second)));
            _processor.MaxConcurrent.Should().Be(1);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("Timed out waiting for the watcher.");
                }
                await Task.Delay(20);
            }
        }

        private sealed class FakeProcessor : IInstantSyncProcessor
        {
            private int _calls;
            private int _running;
            private int _maxConcurrent;

            public ConcurrentQueue<(IReadOnlyCollection<string> Changes, IReadOnlyCollection<string> Deletes)> Calls { get; } = new();

            /// <summary>Given the 1-based call number and the pass's token.</summary>
            public Func<int, CancellationToken, Task<BackupResult>> Behaviour { get; set; } = (_, _) => Task.FromResult(new BackupResult());

            public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

            public async Task<BackupResult> ProcessBatchAsync(InstantSyncItem item, IReadOnlyCollection<string> changedPaths,
                IReadOnlyCollection<string> deletedPaths, IOperationLogger log, CancellationToken cancellationToken)
            {
                var running = Interlocked.Increment(ref _running);
                int max;
                while ((max = Volatile.Read(ref _maxConcurrent)) < running
                       && Interlocked.CompareExchange(ref _maxConcurrent, running, max) != max)
                {
                }

                try
                {
                    var call = Interlocked.Increment(ref _calls);
                    Calls.Enqueue((changedPaths.ToList(), deletedPaths.ToList()));
                    return await Behaviour(call, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                }
            }
        }
    }
}
