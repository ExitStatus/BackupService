using System.Text;
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
            _sut = new FolderPairSynchronizer(new SingleFsEndpointFactory(_fs));
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

        private Task<BackupResult> Run(FolderPair pair) => _sut.SyncAsync(pair, null, null, _log, CancellationToken.None);

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
        public async Task BytesCopied_SumsCopiedAndUpdatedFileSizes_SkippedFilesContributeNothing()
        {
            _fs.AddFile(@"C:\src\new.txt", T1, "hello");  // 5 bytes — new copy
            _fs.AddFile(@"C:\src\upd.txt", T2, "world!"); // 6 bytes — update (destination older)
            _fs.AddFile(@"C:\dst\upd.txt", T1, "x");
            _fs.AddFile(@"C:\src\same.txt", T1, "zzz");    // unchanged (equal timestamp) — not re-copied
            _fs.AddFile(@"C:\dst\same.txt", T1, "zzz");

            var result = await Run(Pair());

            result.Copied.Should().Be(1);
            result.Updated.Should().Be(1);
            result.BytesCopied.Should().Be(11); // 5 + 6; the unchanged file adds nothing
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
        public async Task LeftoverTemp_FromInterruptedRun_IsSweptAtStart()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\.b.txt.tmp", T1, "partial"); // leftover temp whose source file is gone

            var result = await Run(Pair()); // AllowDeletions off — the sweep runs regardless

            _fs.FileExists(@"C:\dst\.b.txt.tmp").Should().BeFalse(); // swept
            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();       // normal copy still happens
            result.Copied.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("Removed leftover temp") && m.Contains(".b.txt.tmp"));
        }

        [Test]
        public async Task GenuineSourceFile_MatchingTempPattern_IsNotSwept()
        {
            // A real source file that happens to match the temp pattern must be backed up, not deleted.
            _fs.AddFile(@"C:\src\.config.tmp", T1, "real");
            _fs.AddFile(@"C:\dst\.config.tmp", T1, "real");

            await Run(Pair());

            _fs.FileExists(@"C:\dst\.config.tmp").Should().BeTrue(); // preserved (it's a source file)
            _log.Messages.Should().NotContain(m => m.Contains("Removed leftover temp"));
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
        public async Task LockedFile_IsLoggedAsWarning_NotError_AndRunContinues()
        {
            _fs.AddFile(@"C:\src\locked.txt", T1, "l");
            _fs.AddFile(@"C:\src\good.txt", T1, "g");
            _fs.CopyLockedFail = dest => dest.Contains("locked.txt"); // sharing violation on this file

            var result = await Run(Pair());

            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            result.Copied.Should().Be(1); // good.txt still copied
            _fs.FileExists(@"C:\dst\good.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\locked.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // no temp left behind
            _log.Messages.Should().Contain(m => m.Contains("Skipped") && m.Contains("locked.txt") && m.Contains("in use"));
            _log.Errors.Should().NotContain(m => m.Contains("locked.txt"));
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

        // ---- Progress ----

        [Test]
        public async Task CountFilesAsync_CountsInScopeFiles_RespectingSubfoldersAndFilters()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\src\b.dat", T1, "b");
            _fs.AddFile(@"C:\src\sub\c.txt", T1, "c");

            (await _sut.CountFilesAsync(Pair(includeSubFolders: false), null, CancellationToken.None)).Should().Be(2); // top-level only
            (await _sut.CountFilesAsync(Pair(includeSubFolders: true), null, CancellationToken.None)).Should().Be(3);  // includes nested

            var filtered = Pair(includeSubFolders: true);
            filtered.Filters.Add(Filter(FilterDirection.Include, FilterKind.File, "*.txt"));
            (await _sut.CountFilesAsync(filtered, null, CancellationToken.None)).Should().Be(2); // a.txt + sub/c.txt
        }

        [Test]
        public async Task SyncAsync_ReportsProgressOncePerInScopeFile_RegardlessOfCopyOrSkip()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");          // copied
            _fs.AddFile(@"C:\src\b.txt", T1, "b");          // skipped (equal timestamp at dest)
            _fs.AddFile(@"C:\dst\b.txt", T1, "b");
            _fs.AddFile(@"C:\src\sub\c.txt", T1, "c");      // copied (nested)

            var reported = 0;
            var progress = new CapturingProgress(value => reported += value);

            await _sut.SyncAsync(Pair(includeSubFolders: true), null, null, _log, CancellationToken.None, progress);

            reported.Should().Be(3); // one report per in-scope source file
        }

        [Test]
        public async Task CancelledMidCopy_RemovesTempAndPropagatesCancellation()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            using var cts = new CancellationTokenSource();
            // Cancel while the source is being read, so cancellation lands during the copy (after the
            // crash-safe temp stream is opened) rather than at the top-of-folder guard.
            _fs.OpenReadOverride = _ => new CancelOnReadStream("hello", cts);

            var act = () => _sut.SyncAsync(Pair(), null, null, _log, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // partial temp swept on cancel
            _fs.FileExists(@"C:\dst\a.txt").Should().BeFalse();         // the destination was never committed
        }

        // ---- Cross-filesystem (e.g. local source -> SMB target) ----

        [Test]
        public async Task CrossFilesystem_StreamsFileFromSourceFsToTargetFs_PreservingTimestampAndBytes()
        {
            var sourceFs = new FakeFileSystem();
            sourceFs.AddDirectory(Source);
            sourceFs.AddFile(@"C:\src\a.txt", T1, "hello");

            var targetFs = new FakeFileSystem();
            targetFs.AddDirectory(Target);

            var sut = new FolderPairSynchronizer(new TwoFsEndpointFactory(sourceFs, Source, targetFs));

            var result = await sut.SyncAsync(Pair(), null, null, _log, CancellationToken.None);

            // The file crossed from the source filesystem into the (separate) target filesystem.
            sourceFs.FileExists(@"C:\dst\a.txt").Should().BeFalse();
            targetFs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            targetFs.ContentOf(@"C:\dst\a.txt").Should().Be("hello");
            targetFs.TimeOf(@"C:\dst\a.txt").Should().Be(T1); // source timestamp carried across
            targetFs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Copied.Should().Be(1);
            result.BytesCopied.Should().Be(5);
        }

        // ---- Fakes ----

        // Returns the same filesystem for both sides (source fs == target fs), starting the walk at the
        // pair's configured paths — preserves the single-filesystem behaviour the bulk of the tests assert.
        private sealed class SingleFsEndpointFactory(IBackupFileSystem fs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default) =>
                Task.FromResult(new EndpointFileSystem(fs, configuredPath, NoopDisposable.Instance));
        }

        // Returns a different filesystem for the source and target sides (distinguished by configured path).
        private sealed class TwoFsEndpointFactory(IBackupFileSystem sourceFs, string sourcePath, IBackupFileSystem targetFs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default)
            {
                var fs = string.Equals(configuredPath, sourcePath, StringComparison.OrdinalIgnoreCase) ? sourceFs : targetFs;
                return Task.FromResult(new EndpointFileSystem(fs, configuredPath, NoopDisposable.Instance));
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }

        private sealed class CapturingProgress(Action<int> onReport) : IProgress<int>
        {
            public void Report(int value) => onReport(value);
        }

        // A read stream that cancels the supplied source the first time it's read, so cancellation is
        // observed mid-copy (CopyToAsync passes the token to ReadAsync, which then throws).
        private sealed class CancelOnReadStream(string content, CancellationTokenSource cts) : MemoryStream(Encoding.UTF8.GetBytes(content), writable: false)
        {
            private bool _cancelled;

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_cancelled)
                {
                    _cancelled = true;
                    cts.Cancel();
                }
                return base.ReadAsync(buffer, cancellationToken);
            }
        }

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
            public Func<string, bool>? CopyLockedFail { get; set; }   // arg: destination — throws a sharing violation
            public Func<string, bool>? MoveShouldFail { get; set; }   // arg: destination
            public Func<string, bool>? GetFilesShouldFail { get; set; } // arg: directory
            public Func<string, Stream>? OpenReadOverride { get; set; } // arg: path — supply a custom read stream

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

            public long GetFileSize(string path) =>
                _files.TryGetValue(path, out var e) ? e.Content.Length : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                _files[path] = e with { Time = value };
            }

            public Stream OpenRead(string path)
            {
                if (OpenReadOverride is not null)
                {
                    return OpenReadOverride(path);
                }
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                return new MemoryStream(Encoding.UTF8.GetBytes(e.Content), writable: false);
            }

            public Stream OpenWrite(string path)
            {
                // The crash-safe copy writes to a target temp; the failure predicates target the temp path.
                if (CopyLockedFail?.Invoke(path) == true)
                {
                    throw new IOException($"Locked: {path}", unchecked((int)0x80070020)); // ERROR_SHARING_VIOLATION
                }
                if (CopyShouldFail?.Invoke(path) == true)
                {
                    throw new IOException($"Write failed: {path}");
                }
                // Register the file (with a placeholder timestamp) when the stream is disposed; the engine
                // then stamps it via SetLastWriteTimeUtc.
                return new FakeWriteStream(bytes => _files[path] = new Entry(default, Encoding.UTF8.GetString(bytes)));
            }

            public void CopyFile(string source, string destination, bool overwrite)
            {
                if (CopyLockedFail?.Invoke(destination) == true)
                {
                    throw new IOException($"Locked: {destination}", unchecked((int)0x80070020)); // ERROR_SHARING_VIOLATION
                }
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

            // A write stream that hands the written bytes to a callback on dispose.
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

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null) =>
                throw new NotSupportedException();

            public string? GetZipComment(string path) => throw new NotSupportedException();

            private static bool IsUnder(string path, string directory)
            {
                var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
