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
    /// The Lightroom watcher shares the instant-sync watcher's retry/catch-up behaviour (covered in depth by
    /// <see cref="InstantSyncWatcherServiceTests"/>); these pin down the parts that differ — it always flushes
    /// through its own processor, and a catch-up pass feeds that processor every source file.
    /// </summary>
    [TestFixture]
    public class LightroomArchiveWatcherServiceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private SqliteConnection _connection = null!;
        private DbContextOptions<BackupDbContext> _options = null!;
        private BackupService.UnitTests.Logging.TempLogStore _logStore = null!;
        private FakeProcessor _processor = null!;
        private LightroomArchiveWatcherService _sut = null!;
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
            _logStore = new BackupService.UnitTests.Logging.TempLogStore();
            _processor = new FakeProcessor();

            _sut = new LightroomArchiveWatcherService(factory.Object, _processor,
                new OperationLogFactory(factory.Object, _logStore.Store), Mock.Of<IBackupRunRecorder>(),
                NullLogger<LightroomArchiveWatcherService>.Instance)
            {
                RetryBaseDelay = TimeSpan.FromMilliseconds(100),
                WatchRetryInterval = TimeSpan.FromMilliseconds(100),
            };

            _root = Path.Combine(Path.GetTempPath(), "bs-lrwatch-" + Guid.NewGuid().ToString("N"));
            _source = Path.Combine(_root, "source");
            Directory.CreateDirectory(Path.Combine(_source, "2026"));
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

        private int SeedProfile()
        {
            using var db = new BackupDbContext(_options);
            var profile = new Profile
            {
                Name = "Photos",
                Type = ProfileType.LightroomArchive,
                Enabled = true,
                DateCreated = DateTimeOffset.UtcNow,
                LightroomArchiveItems =
                {
                    new LightroomArchiveItem
                    {
                        Name = "Exports",
                        SourceFolder = _source,
                        TargetFolder = Path.Combine(_root, "target"),
                        DebounceMilliseconds = 50,
                        IncludeSubFolders = true,
                    },
                },
            };
            db.Profiles.Add(profile);
            db.SaveChanges();
            return profile.Id;
        }

        private string WriteSourceFile(string relativePath)
        {
            var path = Path.Combine(_source, relativePath);
            File.WriteAllText(path, "content");
            return path;
        }

        [Test]
        public async Task AFlushThatFails_IsRetried_WithTheSameChanges()
        {
            _processor.Behaviour = call => call == 1
                ? throw new IOException("The target is unavailable.")
                : new BackupResult();
            await _sut.SyncAsync(SeedProfile());

            var file = WriteSourceFile("a.jpg");

            await WaitUntilAsync(() => _processor.Calls.Count >= 2);
            _processor.Calls.ElementAt(1).Should().Contain(file);
        }

        [Test]
        public async Task ACatchUpPass_FeedsTheProcessorEverySourceFile_NotJustTheChangedOnes()
        {
            // Files that were already there when a copy failed (or while the watcher was down) are only reached by a
            // catch-up — the processor's copy-if-changed makes the unchanged ones cheap.
            var untouched = WriteSourceFile(Path.Combine("2026", "older.jpg"));
            _processor.Behaviour = call => call == 1 ? new BackupResult { Warnings = 1 } : new BackupResult();
            await _sut.SyncAsync(SeedProfile());

            WriteSourceFile("new.jpg");

            await WaitUntilAsync(() => _processor.Calls.Count >= 2);
            _processor.Calls.ElementAt(1).Should().Contain(untouched);
        }

        [Test]
        public async Task AnItemWhoseSourceFolderIsMissing_IsWatchedOnceItAppears_AndCaughtUp()
        {
            Directory.Delete(_source, recursive: true);
            var profileId = SeedProfile();
            await _sut.SyncAsync(profileId);
            _sut.WatcherCount(profileId).Should().Be(0);

            Directory.CreateDirectory(_source);
            var existing = WriteSourceFile("waiting.jpg");

            await WaitUntilAsync(() => _sut.WatcherCount(profileId) == 1);
            await WaitUntilAsync(() => _processor.Calls.Any(c => c.Contains(existing)));
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

        private sealed class FakeProcessor : ILightroomArchiveProcessor
        {
            private int _calls;

            public ConcurrentQueue<IReadOnlyCollection<string>> Calls { get; } = new();

            /// <summary>Given the 1-based call number.</summary>
            public Func<int, BackupResult> Behaviour { get; set; } = _ => new BackupResult();

            public Task<BackupResult> ProcessBatchAsync(LightroomArchiveItem item, int? targetConnectionId, LightroomArchiveSettings settings,
                IReadOnlyCollection<string> changedPaths, IReadOnlyCollection<string> deletedPaths, IOperationLogger log,
                IProgress<int>? progress, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _calls);
                Calls.Enqueue(changedPaths.ToList());
                return Task.FromResult(Behaviour(call));
            }
        }
    }
}
