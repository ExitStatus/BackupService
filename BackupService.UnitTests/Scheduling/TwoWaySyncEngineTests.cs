using System.Text;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using BackupService.Scheduling.TwoWaySync;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class TwoWaySyncEngineTests
    {
        private const string Left = @"C:\left";
        private const string Right = @"C:\right";

        private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime T2 = new(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc); // newer than T1
        private static readonly DateTime T3 = new(2026, 1, 3, 10, 0, 0, DateTimeKind.Utc); // newer than T2

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private string _stateDir = null!;
        private TwoWaySyncStateStore _store = null!;
        private TwoWaySyncEngine _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Left);
            _fs.AddDirectory(Right);
            _log = new CapturingLogger();
            _stateDir = Path.Combine(Path.GetTempPath(), "twoway-tests-" + Guid.NewGuid().ToString("N"));
            _store = new TwoWaySyncStateStore(_stateDir);
            _sut = new TwoWaySyncEngine(new SingleFsEndpointFactory(_fs), _store);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_stateDir, recursive: true); } catch { /* best-effort */ }
        }

        private static TwoWaySyncItem Item(
            ConflictResolution conflict = ConflictResolution.NewerWins,
            bool propagateDeletions = true,
            bool includeSubFolders = true) => new()
            {
                Id = 1,
                Name = "T",
                SourceFolder = Left,
                TargetFolder = Right,
                IncludeSubFolders = includeSubFolders,
                ConflictResolution = conflict,
                PropagateDeletions = propagateDeletions,
            };

        private Task<BackupResult> Run(TwoWaySyncItem item) => _sut.SyncAsync(item, null, null, _log, CancellationToken.None);

        [Test]
        public async Task NewFileOnLeft_IsCopiedToRight_AndBaselineRecorded()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "hello");

            var result = await Run(Item());

            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\right\a.txt").Should().Be("hello");
            _fs.TimeOf(@"C:\right\a.txt").Should().Be(T1);
            result.Copied.Should().Be(1);

            // A second run is a no-op — the baseline now knows about a.txt on both sides.
            var second = await Run(Item());
            second.Copied.Should().Be(0);
            second.Updated.Should().Be(0);
            second.Deleted.Should().Be(0);
        }

        [Test]
        public async Task NewFileOnRight_IsCopiedToLeft()
        {
            _fs.AddFile(@"C:\right\b.txt", T1, "world");

            var result = await Run(Item());

            _fs.FileExists(@"C:\left\b.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\left\b.txt").Should().Be("world");
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task DeletionOnLeft_PropagatesToRight_WhenEnabled()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "hello");
            await Run(Item()); // establish baseline (a.txt on both sides)

            _fs.DeleteFile(@"C:\left\a.txt");
            var result = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\right\a.txt").Should().BeFalse();
            result.Deleted.Should().Be(1);
        }

        [Test]
        public async Task DeletionOnLeft_IsRestored_WhenPropagateDeletionsOff()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "hello");
            await Run(Item(propagateDeletions: false)); // baseline established

            _fs.DeleteFile(@"C:\left\a.txt");
            var result = await Run(Item(propagateDeletions: false));

            _fs.FileExists(@"C:\left\a.txt").Should().BeTrue();       // restored from the surviving side
            _fs.ContentOf(@"C:\left\a.txt").Should().Be("hello");
            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            result.Deleted.Should().Be(0);
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task Conflict_NewerWins_CopiesTheNewerSideOverTheOlder()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item()); // baseline

            _fs.AddFile(@"C:\left\a.txt", T3, "left-edit");   // both edited since baseline
            _fs.AddFile(@"C:\right\a.txt", T2, "right-edit"); // left is newer (T3 > T2)

            var result = await Run(Item(conflict: ConflictResolution.NewerWins));

            _fs.ContentOf(@"C:\left\a.txt").Should().Be("left-edit");
            _fs.ContentOf(@"C:\right\a.txt").Should().Be("left-edit"); // newer (left) won
            result.Updated.Should().Be(1);
        }

        [Test]
        public async Task Conflict_SourceWins_TargetTakesTheSourceCopy()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item());

            _fs.AddFile(@"C:\left\a.txt", T2, "left-edit");   // source older...
            _fs.AddFile(@"C:\right\a.txt", T3, "right-edit"); // ...target newer, but SourceWins ignores that

            await Run(Item(conflict: ConflictResolution.SourceWins));

            _fs.ContentOf(@"C:\right\a.txt").Should().Be("left-edit");
        }

        [Test]
        public async Task Conflict_TargetWins_SourceTakesTheTargetCopy()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item());

            _fs.AddFile(@"C:\left\a.txt", T3, "left-edit");
            _fs.AddFile(@"C:\right\a.txt", T2, "right-edit");

            await Run(Item(conflict: ConflictResolution.TargetWins));

            _fs.ContentOf(@"C:\left\a.txt").Should().Be("right-edit");
        }

        [Test]
        public async Task Conflict_Skip_LeavesBothUntouched_AndWarns()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item());

            _fs.AddFile(@"C:\left\a.txt", T3, "left-edit");
            _fs.AddFile(@"C:\right\a.txt", T2, "right-edit");

            var result = await Run(Item(conflict: ConflictResolution.Skip));

            _fs.ContentOf(@"C:\left\a.txt").Should().Be("left-edit");   // untouched
            _fs.ContentOf(@"C:\right\a.txt").Should().Be("right-edit"); // untouched
            result.Warnings.Should().Be(1);
            result.Copied.Should().Be(0);
            result.Updated.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("Conflict") && m.Contains("skipped"));
        }

        [Test]
        public async Task Conflict_KeepBoth_KeepsSourceCanonical_AndTargetAsConflictCopy_OnBothSides()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item());

            _fs.AddFile(@"C:\left\a.txt", T3, "left-edit");
            _fs.AddFile(@"C:\right\a.txt", T2, "right-edit");

            await Run(Item(conflict: ConflictResolution.KeepBoth));

            // Canonical name holds the source (left) edit on both sides.
            _fs.ContentOf(@"C:\left\a.txt").Should().Be("left-edit");
            _fs.ContentOf(@"C:\right\a.txt").Should().Be("left-edit");

            // A "(conflict …)" copy holds the target (right) edit, present on both sides.
            var leftConflict = _fs.NamesIn(Left).Single(n => n.Contains("(conflict"));
            var rightConflict = _fs.NamesIn(Right).Single(n => n.Contains("(conflict"));
            _fs.ContentOf(Path.Combine(Left, leftConflict)).Should().Be("right-edit");
            _fs.ContentOf(Path.Combine(Right, rightConflict)).Should().Be("right-edit");
        }

        [Test]
        public async Task ModifyVsDelete_NewerWins_KeepsTheEditedCopy()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "base");
            await Run(Item());

            _fs.DeleteFile(@"C:\left\a.txt");             // deleted on the left
            _fs.AddFile(@"C:\right\a.txt", T3, "edited"); // edited on the right

            var result = await Run(Item(conflict: ConflictResolution.NewerWins));

            _fs.FileExists(@"C:\left\a.txt").Should().BeTrue();       // the edit beat the deletion
            _fs.ContentOf(@"C:\left\a.txt").Should().Be("edited");
            result.Deleted.Should().Be(0);
        }

        [Test]
        public async Task BothDeleted_IsANoOp_AndDropsBaseline()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "hello");
            await Run(Item()); // baseline has a.txt

            _fs.DeleteFile(@"C:\left\a.txt");
            _fs.DeleteFile(@"C:\right\a.txt");

            var result = await Run(Item());

            result.Copied.Should().Be(0);
            result.Deleted.Should().Be(0);
            result.Errors.Should().Be(0);
        }

        [Test]
        public async Task NestedFiles_AreReconciled_WhenIncludeSubFolders()
        {
            _fs.AddFile(@"C:\left\sub\c.txt", T1, "nested");

            var result = await Run(Item(includeSubFolders: true));

            _fs.FileExists(@"C:\right\sub\c.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\right\sub\c.txt").Should().Be("nested");
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task NestedFiles_AreIgnored_WhenIncludeSubFoldersOff()
        {
            _fs.AddFile(@"C:\left\top.txt", T1, "t");
            _fs.AddFile(@"C:\left\sub\c.txt", T1, "n");

            var result = await Run(Item(includeSubFolders: false));

            _fs.FileExists(@"C:\right\top.txt").Should().BeTrue();
            _fs.FileExists(@"C:\right\sub\c.txt").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task DisjointAdds_MergeBothSides_AndSecondRunIsNoOp()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            _fs.AddFile(@"C:\right\b.txt", T1, "b");

            var result = await Run(Item());

            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\left\b.txt").Should().BeTrue();
            result.Copied.Should().Be(2);

            var second = await Run(Item());
            second.Copied.Should().Be(0);
            second.Updated.Should().Be(0);
            second.Deleted.Should().Be(0);
        }

        [Test]
        public async Task ExcludeFilter_LeavesMatchingFilesUntouchedOnBothSides()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            _fs.AddFile(@"C:\left\build.tmp", T1, "b");
            var item = Item();
            item.Filters.Add(new TwoWaySyncFilter { Direction = FilterDirection.Exclude, Kind = FilterKind.File, Pattern = "*.tmp" });

            var result = await Run(item);

            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\right\build.tmp").Should().BeFalse(); // excluded — not synced
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task CountFilesAsync_CountsDistinctFilesAcrossBothSides()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");    // only left
            _fs.AddFile(@"C:\right\b.txt", T1, "b");   // only right
            _fs.AddFile(@"C:\left\c.txt", T1, "c");    // both sides — counted once
            _fs.AddFile(@"C:\right\c.txt", T1, "c");

            var count = await _sut.CountFilesAsync(Item(), null, null, CancellationToken.None);

            count.Should().Be(3); // a, b, c
        }

        // ---- Fakes ----

        private sealed class SingleFsEndpointFactory(IBackupFileSystem fs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default) =>
                Task.FromResult(new EndpointFileSystem(fs, configuredPath, NoopDisposable.Instance));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }

        private sealed class CapturingLogger : IOperationLogger
        {
            private readonly List<(OperationLogLevel Level, string Message)> _entries = [];

            public int OperationLogId => 1;

            public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToList();

            public Task AppendAsync(params string[] messages)
            {
                foreach (var m in messages) _entries.Add((OperationLogLevel.Info, m));
                return Task.CompletedTask;
            }

            public Task AppendAsync(OperationLogLevel level, params string[] messages)
            {
                foreach (var m in messages) _entries.Add((level, m));
                return Task.CompletedTask;
            }

            public Task ErrorAsync(string message, Exception? exception = null)
            {
                _entries.Add((OperationLogLevel.Error, exception is null ? message : $"{message}: {exception.Message}"));
                return Task.CompletedTask;
            }

            public Task SetSummaryAsync(string message, OperationLogLevel level) => Task.CompletedTask;
        }

        private sealed class FakeFileSystem : IBackupFileSystem
        {
            private sealed record Entry(DateTime Time, string Content);

            private readonly Dictionary<string, Entry> _files = new(FakeFsPath.Comparer);
            private readonly HashSet<string> _dirs = new(FakeFsPath.Comparer);

            public void AddDirectory(string path)
            {
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    _dirs.Add(current);
                    current = FakeFsPath.Parent(current);
                }
            }

            public void AddFile(string path, DateTime time, string content)
            {
                AddDirectory(FakeFsPath.Parent(path));
                _files[path] = new Entry(time, content);
            }

            public string ContentOf(string path) => _files[path].Content;

            public DateTime TimeOf(string path) => _files[path].Time;

            public IReadOnlyList<string> NamesIn(string directory) =>
                _files.Keys
                    .Where(f => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(f), directory))
                    .Select(f => Path.GetFileName(FakeFsPath.Norm(f))!)
                    .ToList();

            public bool DirectoryExists(string path) => _dirs.Contains(path);

            public void CreateDirectory(string path) => AddDirectory(path);

            public void DeleteDirectory(string path, bool recursive) => throw new NotSupportedException();

            public bool FileExists(string path) => _files.ContainsKey(path);

            public IReadOnlyList<string> GetFiles(string directory)
            {
                if (!_dirs.Contains(directory))
                {
                    throw new DirectoryNotFoundException(directory);
                }
                return _files.Keys
                    .Where(f => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(f), directory))
                    .Select(FakeFsPath.Norm)
                    .ToList();
            }

            public IReadOnlyList<string> GetDirectories(string directory) =>
                _dirs.Where(d => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(d), directory))
                    .Select(FakeFsPath.Norm)
                    .ToList();

            public DateTime GetLastWriteTimeUtc(string path) =>
                _files.TryGetValue(path, out var e) ? e.Time : throw new FileNotFoundException(path);

            public long GetFileSize(string path) =>
                _files.TryGetValue(path, out var e) ? e.Content.Length : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                if (value < DateTime.FromFileTimeUtc(0))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Not a valid Win32 FileTime.");
                }
                _files[path] = e with { Time = value };
            }

            public Stream OpenRead(string path)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                return new MemoryStream(Encoding.UTF8.GetBytes(e.Content), writable: false);
            }

            public Stream OpenWrite(string path) =>
                new FakeWriteStream(bytes => _files[path] = new Entry(default, Encoding.UTF8.GetString(bytes)));

            public void CopyFile(string source, string destination, bool overwrite) => throw new NotSupportedException();

            public void MoveFile(string source, string destination, bool overwrite)
            {
                if (!_files.TryGetValue(source, out var e))
                {
                    throw new FileNotFoundException(source);
                }
                if (_files.ContainsKey(destination) && !overwrite)
                {
                    throw new IOException($"File exists: {destination}");
                }
                _files[destination] = e;
                _files.Remove(source);
            }

            public void DeleteFile(string path)
            {
                if (!_files.Remove(path))
                {
                    throw new FileNotFoundException(path);
                }
            }

            public bool FilesContentEqual(string a, string b) =>
                _files.TryGetValue(a, out var ea) && _files.TryGetValue(b, out var eb) && ea.Content == eb.Content;

            public string GetTempFilePath(string fileName) => throw new NotSupportedException();

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null) =>
                throw new NotSupportedException();

            public string? GetZipComment(string path) => throw new NotSupportedException();

            private sealed class FakeWriteStream(Action<byte[]> onClose) : MemoryStream
            {
                private bool _done;

                protected override void Dispose(bool disposing)
                {
                    if (!_done)
                    {
                        _done = true;
                        onClose(ToArray());
                    }
                    base.Dispose(disposing);
                }
            }
        }
    }
}
