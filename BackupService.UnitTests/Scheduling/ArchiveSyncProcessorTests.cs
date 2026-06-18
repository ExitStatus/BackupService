using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class ArchiveSyncProcessorTests
    {
        private const string Source = @"C:\src";
        private const string Target = @"C:\dst";

        private static readonly DateTime RunTime = new(2026, 6, 18, 12, 0, 0, DateTimeKind.Local);

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private ArchiveSyncProcessor _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Source);
            _fs.AddDirectory(Target);
            _log = new CapturingLogger();
            _sut = new ArchiveSyncProcessor(_fs);
        }

        private static ArchiveSyncItem KeepLastN(int count) => new()
        {
            Name = "A",
            SourceFolder = Source,
            TargetFolder = Target,
            FileName = "Backup",
            RetentionMode = ArchiveRetentionMode.KeepLastN,
            RetentionCount = count,
            MaxLevels = 1,
        };

        private static ArchiveSyncItem Gfs(int perLevel, int maxLevels) => new()
        {
            Name = "A",
            SourceFolder = Source,
            TargetFolder = Target,
            FileName = "Backup",
            RetentionMode = ArchiveRetentionMode.GrandfatherFatherSon,
            RetentionCount = perLevel,
            MaxLevels = maxLevels,
        };

        // Names follow the processor's scheme so ListArchives parses them.
        private static string KeepName(DateTime ts) => $@"C:\dst\Backup_{ts:yyyy-MM-dd_HHmmss}.zip";
        private static string GfsName(int level, DateTime ts) => $@"C:\dst\Backup_L{level}_{ts:yyyy-MM-dd_HHmmss}.zip";

        [Test]
        public async Task CreatesArchive_LeavingNoTemp()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");

            var result = await _sut.CreateArchiveAsync(KeepLastN(5), runIndex: 1, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            result.Errors.Should().Be(0);
            _fs.FileExists(KeepName(RunTime)).Should().BeTrue();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            _log.Messages.Should().Contain(m => m.Contains("Created archive"));
            // Verbose: the archived files are logged as a single Debug detail row.
            _log.DebugMessages.Should().ContainSingle()
                .Which.Should().Contain("Archived 1 file(s):").And.Contain("file.txt");
        }

        [Test]
        public async Task VerboseLogsFiles_AsSingleRow_CrlfSeparated_IncludingSubFolders()
        {
            _fs.AddFile(@"C:\src\top.txt", RunTime, "a");
            _fs.AddFile(@"C:\src\sub\nested.txt", RunTime, "b");
            var item = KeepLastN(5);
            item.IncludeSubFolders = true;

            await _sut.CreateArchiveAsync(item, runIndex: 1, RunTime, _log, CancellationToken.None);

            // One detail row, the file list split across CRLF-separated lines.
            var detail = _log.DebugMessages.Should().ContainSingle().Subject;
            detail.Should().Contain("Archived 2 file(s):");
            detail.Should().Contain("top.txt");
            detail.Should().Contain("sub/nested.txt");
            detail.Should().Contain("\r\n");
        }

        [Test]
        public async Task SourceMissing_ReportsError_NoArchive()
        {
            var item = KeepLastN(5);
            item.SourceFolder = @"C:\does-not-exist";

            var result = await _sut.CreateArchiveAsync(item, runIndex: 1, RunTime, _log, CancellationToken.None);

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(0);
            _fs.GetFiles(Target).Should().BeEmpty();
        }

        [Test]
        public async Task KeepLastN_PrunesOldestBeyondN()
        {
            // Three existing archives plus the one created this run = 4; keep 3 → oldest pruned.
            var t1 = RunTime.AddDays(-3);
            var t2 = RunTime.AddDays(-2);
            var t3 = RunTime.AddDays(-1);
            _fs.AddFile(KeepName(t1), t1, "z");
            _fs.AddFile(KeepName(t2), t2, "z");
            _fs.AddFile(KeepName(t3), t3, "z");

            var result = await _sut.CreateArchiveAsync(KeepLastN(3), runIndex: 4, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            result.Deleted.Should().Be(1);
            _fs.FileExists(KeepName(t1)).Should().BeFalse(); // oldest pruned
            _fs.FileExists(KeepName(t2)).Should().BeTrue();
            _fs.FileExists(KeepName(t3)).Should().BeTrue();
            _fs.FileExists(KeepName(RunTime)).Should().BeTrue(); // newest
        }

        [Test]
        public async Task Gfs_PromotesOldestToNextLevel_OnCadence()
        {
            // N=3, maxLevels=3. Level 1 already full (3 sons); runIndex 3 is divisible by N → promote.
            var s1 = RunTime.AddHours(-3);
            var s2 = RunTime.AddHours(-2);
            var s3 = RunTime.AddHours(-1);
            _fs.AddFile(GfsName(1, s1), s1, "z");
            _fs.AddFile(GfsName(1, s2), s2, "z");
            _fs.AddFile(GfsName(1, s3), s3, "z");

            var result = await _sut.CreateArchiveAsync(Gfs(3, 3), runIndex: 3, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            // The oldest son (s1) is promoted to level 2, not deleted.
            _fs.FileExists(GfsName(1, s1)).Should().BeFalse();
            _fs.FileExists(GfsName(2, s1)).Should().BeTrue();
            // Level 1 keeps the newest 3 (s2, s3, and the new son).
            _fs.FileExists(GfsName(1, s2)).Should().BeTrue();
            _fs.FileExists(GfsName(1, s3)).Should().BeTrue();
            _fs.FileExists(GfsName(1, RunTime)).Should().BeTrue();
            result.Deleted.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("Promoted") && m.Contains("level 2"));
        }

        [Test]
        public async Task Gfs_WhenNotOnCadence_DeletesOldestSon_NoPromotion()
        {
            // N=3, maxLevels=3. Level 1 full; runIndex 4 is NOT divisible by N → no promotion, oldest deleted.
            var s1 = RunTime.AddHours(-3);
            var s2 = RunTime.AddHours(-2);
            var s3 = RunTime.AddHours(-1);
            _fs.AddFile(GfsName(1, s1), s1, "z");
            _fs.AddFile(GfsName(1, s2), s2, "z");
            _fs.AddFile(GfsName(1, s3), s3, "z");

            var result = await _sut.CreateArchiveAsync(Gfs(3, 3), runIndex: 4, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            result.Deleted.Should().Be(1);
            _fs.FileExists(GfsName(1, s1)).Should().BeFalse(); // oldest son deleted
            _fs.FileExists(GfsName(2, s1)).Should().BeFalse(); // not promoted
            _fs.FileExists(GfsName(1, RunTime)).Should().BeTrue();
        }

        // ---- Fakes ----

        private sealed class CapturingLogger : IOperationLogger
        {
            private readonly List<(OperationLogLevel Level, string Message)> _entries = [];

            public int OperationLogId => 1;

            public IReadOnlyList<string> Messages => _entries.Select(e => e.Message).ToList();

            public IReadOnlyList<string> Errors =>
                _entries.Where(e => e.Level == OperationLogLevel.Error).Select(e => e.Message).ToList();

            public IReadOnlyList<string> DebugMessages =>
                _entries.Where(e => e.Level == OperationLogLevel.Debug).Select(e => e.Message).ToList();

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
            private int _tempCounter;

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

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (_files.TryGetValue(path, out var e))
                {
                    _files[path] = e with { Time = value };
                }
            }

            public void CopyFile(string source, string destination, bool overwrite)
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

            public string GetTempFilePath(string fileName)
            {
                var dir = $@"C:\temp\{_tempCounter++}";
                AddDirectory(dir);
                return Path.Combine(dir, fileName);
            }

            public IReadOnlyList<string> CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders)
            {
                // Stand in for a real zip — record a file at the destination so the copy can read it,
                // and return the relative entry names of the source files (top-level or recursive).
                var entries = _files.Keys
                    .Where(f => includeSubfolders
                        ? IsUnder(f, sourceDirectory)
                        : string.Equals(Path.GetDirectoryName(f), sourceDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(sourceDirectory, f).Replace('\\', '/'))
                    .ToList();
                AddFile(destinationZip, DateTime.UtcNow, "zip");
                return entries;
            }

            private static bool IsUnder(string path, string directory)
            {
                var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
