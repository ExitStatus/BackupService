using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using FluentAssertions;

namespace BackupService.UnitTests.Scheduling
{
    [TestFixture]
    public class FolderPairSynchronizerTests
    {
        private const string Source = @"C:\src";
        private const string Target = @"C:\dst";

        private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime T2 = new(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc); // newer than T1

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private FolderPairSynchronizer _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Source);
            _log = new CapturingLogger();
            _sut = new FolderPairSynchronizer(_fs);
        }

        private static FolderPair Pair(
            bool allowDeletions = false,
            bool includeSubFolders = false,
            OverwriteBehaviour overwrite = OverwriteBehaviour.DoNotOverwriteNewer) => new()
            {
                Name = "P",
                SourceFolder = Source,
                TargetFolder = Target,
                AllowDeletions = allowDeletions,
                IncludeSubFolders = includeSubFolders,
                OverwriteBehaviour = overwrite,
            };

        private Task<BackupResult> Run(FolderPair pair) => _sut.SyncAsync(pair, _log, CancellationToken.None);

        [Test]
        public async Task NewFile_IsCopiedThroughTemp_LeavingNoTemp()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");

            var result = await Run(Pair());

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("hello");
            _fs.TimeOf(@"C:\dst\a.txt").Should().Be(T1); // copy preserves the source timestamp
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // no temp left behind
            result.Copied.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Copied") && m.Contains("a.txt"));
        }

        [Test]
        public async Task SourceNewer_OverwritesDestination()
        {
            _fs.AddFile(@"C:\src\a.txt", T2, "new");
            _fs.AddFile(@"C:\dst\a.txt", T1, "old");

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("new");
            result.Updated.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Updated") && m.Contains("a.txt"));
        }

        [Test]
        public async Task EqualTimestamp_IsSkippedWithNoLog()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "src");
            _fs.AddFile(@"C:\dst\a.txt", T1, "dst"); // same stamp, different content

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("dst"); // untouched
            result.Copied.Should().Be(0);
            result.Updated.Should().Be(0);
            _log.Messages.Should().NotContain(m => m.Contains("a.txt"));
        }

        [Test]
        public async Task DestinationNewer_DoNotOverwrite_IsSkipped()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "src");
            _fs.AddFile(@"C:\dst\a.txt", T2, "dst"); // destination newer

            var result = await Run(Pair(overwrite: OverwriteBehaviour.DoNotOverwriteNewer));

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("dst");
            result.Updated.Should().Be(0);
            _log.Messages.Should().NotContain(m => m.Contains("a.txt"));
        }

        [Test]
        public async Task DestinationNewer_AlwaysOverwrite_Overwrites()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "src");
            _fs.AddFile(@"C:\dst\a.txt", T2, "dst");

            var result = await Run(Pair(overwrite: OverwriteBehaviour.AlwaysOverwrite));

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("src");
            _fs.TimeOf(@"C:\dst\a.txt").Should().Be(T1);
            result.Updated.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Overwrote"));
        }

        [Test]
        public async Task DestinationNewer_UpdateOnlyIfContentMatches_ContentEqual_SyncsTimestamp()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "same");
            _fs.AddFile(@"C:\dst\a.txt", T2, "same"); // newer, identical content

            var result = await Run(Pair(overwrite: OverwriteBehaviour.UpdateOnlyIfContentMatches));

            _fs.TimeOf(@"C:\dst\a.txt").Should().Be(T1); // timestamp synced to source
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("same");
            result.Updated.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Synced timestamp"));
        }

        [Test]
        public async Task DestinationNewer_UpdateOnlyIfContentMatches_ContentDiffers_IsSkipped()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "src");
            _fs.AddFile(@"C:\dst\a.txt", T2, "dst"); // newer, different content

            var result = await Run(Pair(overwrite: OverwriteBehaviour.UpdateOnlyIfContentMatches));

            _fs.TimeOf(@"C:\dst\a.txt").Should().Be(T2); // untouched
            result.Updated.Should().Be(0);
            _log.Messages.Should().NotContain(m => m.Contains("a.txt"));
        }

        [Test]
        public async Task AllowDeletions_RemovesOrphanTargetFile()
        {
            _fs.AddFile(@"C:\src\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\orphan.txt", T1, "o");

            var result = await Run(Pair(allowDeletions: true));

            _fs.FileExists(@"C:\dst\orphan.txt").Should().BeFalse();
            result.Deleted.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Deleted") && m.Contains("orphan.txt"));
        }

        [Test]
        public async Task WithoutAllowDeletions_OrphanTargetFileIsKept()
        {
            _fs.AddFile(@"C:\src\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\orphan.txt", T1, "o");

            var result = await Run(Pair(allowDeletions: false));

            _fs.FileExists(@"C:\dst\orphan.txt").Should().BeTrue();
            result.Deleted.Should().Be(0);
        }

        [Test]
        public async Task IncludeSubFolders_RecursesAndCopiesNestedFiles()
        {
            _fs.AddFile(@"C:\src\top.txt", T1, "t");
            _fs.AddFile(@"C:\src\sub\nested.txt", T1, "n");

            var result = await Run(Pair(includeSubFolders: true));

            _fs.FileExists(@"C:\dst\top.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\sub\nested.txt").Should().BeTrue();
            result.Copied.Should().Be(2);
        }

        [Test]
        public async Task WithoutIncludeSubFolders_NestedFilesAreNotCopied()
        {
            _fs.AddFile(@"C:\src\top.txt", T1, "t");
            _fs.AddFile(@"C:\src\sub\nested.txt", T1, "n");

            var result = await Run(Pair(includeSubFolders: false));

            _fs.FileExists(@"C:\dst\top.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\sub\nested.txt").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task AllowDeletionsWithSubFolders_RemovesOrphanSubFolderAndItsFiles()
        {
            _fs.AddFile(@"C:\src\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\keep.txt", T1, "k");
            _fs.AddFile(@"C:\dst\gone\stale.txt", T1, "s"); // sub-folder not present in source

            var result = await Run(Pair(allowDeletions: true, includeSubFolders: true));

            _fs.FileExists(@"C:\dst\gone\stale.txt").Should().BeFalse();
            _fs.DirectoryExists(@"C:\dst\gone").Should().BeFalse();
            _log.Messages.Should().Contain(m => m.Contains("Deleted folder") && m.Contains("gone"));
        }

        [Test]
        public async Task CopyFailure_LogsErrorAndContinuesWithOtherFiles()
        {
            _fs.AddFile(@"C:\src\bad.txt", T1, "b");
            _fs.AddFile(@"C:\src\good.txt", T1, "g");
            // Fail the temp write for bad.txt only.
            _fs.CopyShouldFail = dest => dest.Contains("bad.txt");

            var result = await Run(Pair());

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(1); // good.txt still copied
            _fs.FileExists(@"C:\dst\good.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\bad.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // no temp left
            _log.Errors.Should().Contain(m => m.Contains("Failed to copy") && m.Contains("bad.txt"));
        }

        [Test]
        public async Task RenameFailure_CleansUpTempAndLogsError()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.MoveShouldFail = dest => dest.Contains("a.txt"); // the rename temp -> dest fails

            var result = await Run(Pair());

            result.Errors.Should().Be(1);
            _fs.FileExists(@"C:\dst\a.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // temp cleaned up
            _log.Errors.Should().Contain(m => m.Contains("Failed to copy"));
        }

        [Test]
        public async Task SourceFolderAccessFailure_LogsErrorAndCopiesNothing()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.GetFilesShouldFail = dir => string.Equals(dir, Source, StringComparison.OrdinalIgnoreCase);

            var result = await Run(Pair());

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(0);
            _log.Errors.Should().Contain(m => m.Contains("Failed to access source folder"));
        }

        // ---- Include/exclude filters ----

        private static FolderPairFilter Filter(FilterDirection direction, FilterKind kind, string pattern) =>
            new() { Direction = direction, Kind = kind, Pattern = pattern };

        [Test]
        public async Task Includes_OnlyMatchingFilesAreCopied()
        {
            _fs.AddFile(@"C:\src\keep.txt", T1, "a");
            _fs.AddFile(@"C:\src\skip.dat", T1, "b");
            var pair = Pair();
            pair.Filters.Add(Filter(FilterDirection.Include, FilterKind.File, "*.txt"));

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\keep.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\skip.dat").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task ExcludeFile_MatchingFileIsNotCopied()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\src\build.tmp", T1, "b");
            var pair = Pair();
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.File, "*.tmp"));

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\build.tmp").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task ExcludeFolder_SubtreeIsNotRecursed()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\src\bin\obj.txt", T1, "b");
            var pair = Pair(includeSubFolders: true);
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.Folder, "bin"));

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\bin\obj.txt").Should().BeFalse(); // excluded folder not recursed
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task ExcludeFolder_ExistingTargetSubtreeIsNotDeleted()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\bin\old.txt", T1, "x"); // target-only folder matching an exclude
            var pair = Pair(allowDeletions: true, includeSubFolders: true);
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.Folder, "bin"));

            await Run(pair);

            _fs.FileExists(@"C:\dst\bin\old.txt").Should().BeTrue(); // excluded subtree left untouched
        }

        [Test]
        public async Task ExcludePath_OnlyThatExactSubtreeIsSkipped_NotByNameElsewhere()
        {
            _fs.AddFile(@"C:\src\bin\obj.txt", T1, "a");        // excluded exact path
            _fs.AddFile(@"C:\src\sub\bin\keep.txt", T1, "b");   // same folder name, different path → kept
            var pair = Pair(includeSubFolders: true);
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.Path, @"bin"));

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\bin\obj.txt").Should().BeFalse();   // exact path excluded
            _fs.FileExists(@"C:\dst\sub\bin\keep.txt").Should().BeTrue(); // not excluded by name
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task ExcludePath_ExistingTargetSubtreeIsNotDeleted()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\logs\old.txt", T1, "x"); // target-only folder matching an exclude path
            var pair = Pair(allowDeletions: true, includeSubFolders: true);
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.Path, @"logs"));

            await Run(pair);

            _fs.FileExists(@"C:\dst\logs\old.txt").Should().BeTrue(); // excluded path left untouched
        }

        [Test]
        public async Task Deletions_RemoveInScopeOrphans_ButLeaveExcludedTargetFiles()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\a.txt", T1, "a");       // in sync
            _fs.AddFile(@"C:\dst\orphan.txt", T1, "o");  // in-scope orphan → deleted
            _fs.AddFile(@"C:\dst\keep.tmp", T1, "k");    // out of scope (excluded) → kept
            var pair = Pair(allowDeletions: true);
            pair.Filters.Add(Filter(FilterDirection.Exclude, FilterKind.File, "*.tmp"));

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\orphan.txt").Should().BeFalse();
            _fs.FileExists(@"C:\dst\keep.tmp").Should().BeTrue();
            result.Deleted.Should().Be(1);
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

            // Failure injection: given a path, return true to throw.
            public Func<string, bool>? CopyShouldFail { get; set; }   // arg: destination
            public Func<string, bool>? MoveShouldFail { get; set; }   // arg: destination
            public Func<string, bool>? GetFilesShouldFail { get; set; } // arg: directory

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

            public DateTime TimeOf(string path) => _files[path].Time;

            public bool DirectoryExists(string path) => _dirs.Contains(path);

            public void CreateDirectory(string path) => AddDirectory(path);

            public void DeleteDirectory(string path, bool recursive)
            {
                var hasChildren = _files.Keys.Any(f => IsUnder(f, path)) || _dirs.Any(d => IsUnder(d, path));
                if (hasChildren && !recursive)
                {
                    throw new IOException($"Directory not empty: {path}");
                }

                foreach (var f in _files.Keys.Where(f => IsUnder(f, path) || string.Equals(Path.GetDirectoryName(f), path, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _files.Remove(f);
                }
                foreach (var d in _dirs.Where(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase) || IsUnder(d, path)).ToList())
                {
                    _dirs.Remove(d);
                }
            }

            public bool FileExists(string path) => _files.ContainsKey(path);

            public IReadOnlyList<string> GetFiles(string directory)
            {
                if (GetFilesShouldFail?.Invoke(directory) == true)
                {
                    throw new UnauthorizedAccessException($"Access denied: {directory}");
                }
                if (!_dirs.Contains(directory))
                {
                    throw new DirectoryNotFoundException(directory);
                }
                return _files.Keys
                    .Where(f => string.Equals(Path.GetDirectoryName(f), directory, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            public IReadOnlyList<string> GetDirectories(string directory) =>
                _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), directory, StringComparison.OrdinalIgnoreCase)).ToList();

            public DateTime GetLastWriteTimeUtc(string path) =>
                _files.TryGetValue(path, out var e) ? e.Time : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                _files[path] = e with { Time = value };
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
                _files[destination] = e; // record is immutable — safe to share
            }

            public void MoveFile(string source, string destination, bool overwrite)
            {
                if (MoveShouldFail?.Invoke(destination) == true)
                {
                    throw new IOException($"Move failed: {destination}");
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
