using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class InstantSyncProcessorTests
    {
        private const string Source = @"C:\src";
        private const string Target = @"C:\dst";

        private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private InstantSyncProcessor _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Source);
            _fs.AddDirectory(Target);
            _log = new CapturingLogger();
            _sut = new InstantSyncProcessor(_fs);
        }

        private static InstantSyncItem Item(bool allowDeletions = false) => new()
        {
            Name = "I",
            SourceFolder = Source,
            TargetFolder = Target,
            DebounceMilliseconds = 1000,
            AllowDeletions = allowDeletions,
        };

        private Task<BackupResult> Run(InstantSyncItem item, string[] changes, string[]? deletes = null) =>
            _sut.ProcessBatchAsync(item, changes, deletes ?? [], _log, CancellationToken.None);

        [Test]
        public async Task ChangedFile_IsCopiedThroughTemp_LeavingNoTemp()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");

            var result = await Run(Item(), [@"C:\src\a.txt"]);

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("hello");
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Copied.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Copied") && m.Contains("a.txt"));
        }

        [Test]
        public async Task ChangedFile_InSubFolder_IsRebasedUnderTargetAndFolderCreated()
        {
            _fs.AddFile(@"C:\src\sub\nested.txt", T1, "n");

            var result = await Run(Item(), [@"C:\src\sub\nested.txt"]);

            _fs.FileExists(@"C:\dst\sub\nested.txt").Should().BeTrue();
            result.Copied.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Created folder") && m.Contains("sub"));
        }

        [Test]
        public async Task ExistingDestination_IsOverwritten()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "new");
            _fs.AddFile(@"C:\dst\a.txt", T1, "old");

            var result = await Run(Item(), [@"C:\src\a.txt"]);

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("new");
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task DeletedSource_RemovesTarget_WhenAllowDeletions()
        {
            _fs.AddFile(@"C:\dst\a.txt", T1, "x");

            var result = await Run(Item(allowDeletions: true), changes: [], deletes: [@"C:\src\a.txt"]);

            _fs.FileExists(@"C:\dst\a.txt").Should().BeFalse();
            result.Deleted.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Deleted") && m.Contains("a.txt"));
        }

        [Test]
        public async Task DeletedSource_IsIgnored_WhenAllowDeletionsOff()
        {
            _fs.AddFile(@"C:\dst\a.txt", T1, "x");

            var result = await Run(Item(allowDeletions: false), changes: [], deletes: [@"C:\src\a.txt"]);

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            result.Deleted.Should().Be(0);
        }

        [Test]
        public async Task ChangedPath_NoLongerExists_IsSkippedSilently()
        {
            // Path was queued but deleted/renamed away before the batch ran — nothing at the source.
            var result = await Run(Item(), [@"C:\src\gone.txt"]);

            _fs.FileExists(@"C:\dst\gone.txt").Should().BeFalse();
            result.Copied.Should().Be(0);
            result.Errors.Should().Be(0);
        }

        [Test]
        public async Task CopyFailure_LogsErrorAndCleansTemp()
        {
            _fs.AddFile(@"C:\src\bad.txt", T1, "b");
            _fs.AddFile(@"C:\src\good.txt", T1, "g");
            _fs.CopyShouldFail = dest => dest.Contains("bad.txt");

            var result = await Run(Item(), [@"C:\src\bad.txt", @"C:\src\good.txt"]);

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(1);
            _fs.FileExists(@"C:\dst\good.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\bad.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            _log.Errors.Should().Contain(m => m.Contains("Failed to copy") && m.Contains("bad.txt"));
        }

        // ---- Fakes ----

        private sealed class CapturingLogger : IOperationLogger
        {
            private readonly List<(OperationLogLevel Level, string Message)> _entries = [];

            public int OperationLogId => 1;

            public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToList();

            public IReadOnlyList<string> Errors =>
                _entries.Where(e => e.Level == OperationLogLevel.Error).Select(e => e.Message).ToList();

            public Task AppendAsync(params string[] messages)
            {
                foreach (var m in messages)
                {
                    _entries.Add((OperationLogLevel.Info, m));
                }
                return Task.CompletedTask;
            }

            public Task AppendAsync(OperationLogLevel level, params string[] messages)
            {
                foreach (var m in messages)
                {
                    _entries.Add((level, m));
                }
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

            private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

            public Func<string, bool>? CopyShouldFail { get; set; } // arg: destination

            public IReadOnlyList<string> AllFiles => _files.Keys.ToList();

            public void AddDirectory(string path)
            {
                var current = path;
                while (!string.IsNullOrEmpty(current))
                {
                    _dirs.Add(current);
                    current = Path.GetDirectoryName(current)!;
                }
            }

            public void AddFile(string path, DateTime time, string content)
            {
                AddDirectory(Path.GetDirectoryName(path)!);
                _files[path] = new Entry(time, content);
            }

            public string ContentOf(string path) => _files[path].Content;

            public bool DirectoryExists(string path) => _dirs.Contains(path);

            public void CreateDirectory(string path) => AddDirectory(path);

            public void DeleteDirectory(string path, bool recursive)
            {
                foreach (var f in _files.Keys.Where(f => IsUnder(f, path)).ToList())
                {
                    _files.Remove(f);
                }
                foreach (var d in _dirs.Where(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase) || IsUnder(d, path)).ToList())
                {
                    _dirs.Remove(d);
                }
            }

            public bool FileExists(string path) => _files.ContainsKey(path);

            public IReadOnlyList<string> GetFiles(string directory) =>
                _files.Keys.Where(f => string.Equals(Path.GetDirectoryName(f), directory, StringComparison.OrdinalIgnoreCase)).ToList();

            public IReadOnlyList<string> GetDirectories(string directory) =>
                _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), directory, StringComparison.OrdinalIgnoreCase)).ToList();

            public DateTime GetLastWriteTimeUtc(string path) =>
                _files.TryGetValue(path, out var e) ? e.Time : throw new FileNotFoundException(path);

            public long GetFileSize(string path) =>
                _files.TryGetValue(path, out var e) ? e.Content.Length : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (_files.TryGetValue(path, out var e))
                {
                    _files[path] = e with { Time = value };
                }
            }

            public void CopyFile(string source, string destination, bool overwrite)
            {
                if (CopyShouldFail?.Invoke(destination) == true)
                {
                    throw new IOException($"Copy failed: {destination}");
                }
                if (!_files.TryGetValue(source, out var e))
                {
                    throw new FileNotFoundException(source);
                }
                if (_files.ContainsKey(destination) && !overwrite)
                {
                    throw new IOException($"File exists: {destination}");
                }
                _files[destination] = e;
            }

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

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null) =>
                throw new NotSupportedException();

            private static bool IsUnder(string path, string directory)
            {
                var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
