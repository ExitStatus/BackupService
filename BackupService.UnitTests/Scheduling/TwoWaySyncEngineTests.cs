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
        public async Task PropagatedDeletion_OfAProtectedFile_IsKeptAsWarning()
        {
            // The scenario behind the guard: an empty copy of a Google Doc is deleted on one side, and the deletion
            // would propagate to the real Doc on Drive. The Drive side refuses, so the Doc survives.
            _fs.AddFile(@"C:\left\Report", T1, "");
            await Run(Item());                        // baseline: Report in sync on both sides
            _fs.Protected.Add(@"C:\right\Report");
            _fs.DeleteFile(@"C:\left\Report");        // the user cleans up the empty copy

            var result = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\right\Report").Should().BeTrue();
            result.Deleted.Should().Be(0);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("Kept") && m.Contains("Report") && m.Contains("Google Docs"));
        }

        [Test]
        public async Task Update_OverAProtectedFile_IsKeptAsWarning()
        {
            _fs.AddFile(@"C:\left\Report", T1, "");
            await Run(Item());
            _fs.Protected.Add(@"C:\right\Report");
            _fs.AddFile(@"C:\left\Report", T3, "local-edit"); // edited on the left — would overwrite the right

            var result = await Run(Item());

            _fs.ContentOf(@"C:\right\Report").Should().Be("");
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp")); // the crash-safe temp was removed
        }

        [Test]
        public async Task FileThatCantBeDownloaded_IsSkippedAsWarning_AndNeverMistakenForADeletion()
        {
            // A Google Docs file on one side can't be downloaded. It's skipped as a warning and left out of the
            // baseline — so the next run sees it as still new on that side, never as "deleted on the other side"
            // (which, with PropagateDeletions, would delete the original).
            _fs.AddFile(@"C:\right\Report", T1, "");
            _fs.NotDownloadable.Add(@"C:\right\Report");

            var first = await Run(Item(propagateDeletions: true));
            var second = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\right\Report").Should().BeTrue();
            _fs.FileExists(@"C:\left\Report").Should().BeFalse();
            first.Warnings.Should().Be(1);
            first.Errors.Should().Be(0);
            second.Warnings.Should().Be(1);
            second.Deleted.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("Skipped") && m.Contains("Google Docs"));
        }

        // ---- A missing folder must never read as "everything was deleted" ----

        [Test]
        public async Task MissingTargetFolder_AfterASync_DeletesNothing_AndCarriesOnOnceItIsBack()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            _fs.AddFile(@"C:\left\sub\b.txt", T1, "b");
            await Run(Item());
            _fs.RemoveDirectory(Right); // the target drive is unplugged / the folder renamed away

            var missing = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\left\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\left\sub\b.txt").Should().BeTrue();
            missing.Deleted.Should().Be(0);
            missing.Errors.Should().Be(1);
            _log.Messages.Should().Contain(m => m.Contains("was not found"));

            // It comes back unchanged: the baseline was kept, so the next run has nothing to do.
            _fs.AddFile(@"C:\right\a.txt", T1, "a");
            _fs.AddFile(@"C:\right\sub\b.txt", T1, "b");
            var back = await Run(Item());
            back.Copied.Should().Be(0);
            back.Updated.Should().Be(0);
            back.Deleted.Should().Be(0);
        }

        [Test]
        public async Task MissingSourceFolder_AfterASync_DeletesNothingFromTheTarget()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            await Run(Item());
            _fs.RemoveDirectory(Left);

            var result = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            result.Deleted.Should().Be(0);
            result.Errors.Should().Be(1);
        }

        [Test]
        public async Task FirstRun_IntoATargetFolderThatDoesNotExistYet_CreatesItAndCopies()
        {
            // With nothing synced yet there's nothing to mistake for deletions, so a missing target is just created.
            _fs.RemoveDirectory(Right);
            _fs.AddFile(@"C:\left\a.txt", T1, "a");

            var result = await Run(Item());

            _fs.ContentOf(@"C:\right\a.txt").Should().Be("a");
            result.Errors.Should().Be(0);
        }

        // ---- The baseline belongs to one folder pair ----

        [Test]
        public async Task RePointedTarget_ResetsTheSyncState_AndDeletesNothing()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            await Run(Item());
            _fs.AddDirectory(@"C:\right2"); // a new, empty target folder
            var item = Item(propagateDeletions: true);
            item.TargetFolder = @"C:\right2";

            var result = await Run(item);

            _fs.FileExists(@"C:\left\a.txt").Should().BeTrue();   // not "deleted on the target"
            _fs.ContentOf(@"C:\right2\a.txt").Should().Be("a");  // copied to the new target instead
            result.Deleted.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("sync state was reset"));
        }

        [Test]
        public async Task ChangedConnection_ResetsTheSyncState_AndDeletesNothing()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            await _sut.SyncAsync(Item(), null, null, _log, CancellationToken.None);
            _fs.DeleteFile(@"C:\right\a.txt"); // the new connection's folder doesn't have it

            var result = await _sut.SyncAsync(Item(propagateDeletions: true), null, 5, _log, CancellationToken.None);

            _fs.FileExists(@"C:\left\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\right\a.txt").Should().BeTrue();
            result.Deleted.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("sync state was reset"));
        }

        [Test]
        public async Task ManifestFromAnOlderVersion_IsTrustedOnce_AndStampedWhenSaved()
        {
            // A baseline written before the endpoint stamp is kept (so a deletion made since the last sync is carried
            // through rather than undone) and gets this pair's stamp on save.
            _fs.AddFile(@"C:\left\a.txt", T1, "a");
            var manifest = Path.Combine(_stateDir, "1.manifest");
            await File.WriteAllTextAsync(manifest, $"{T1.Ticks}\t1\ta.txt\n"); // a.txt was in sync; since deleted on the target

            var result = await Run(Item(propagateDeletions: true));

            _fs.FileExists(@"C:\left\a.txt").Should().BeFalse(); // the target's deletion is propagated
            result.Deleted.Should().Be(1);
            _log.Messages.Should().NotContain(m => m.Contains("sync state was reset"));
            (await File.ReadAllLinesAsync(manifest))[0].Should().StartWith("#endpoints\t");
        }

        // ---- An unsettled file keeps its baseline ----

        [Test]
        public async Task FailedCopy_KeepsTheBaseline_SoTheEditIsNotLaterResolvedAsAConflict()
        {
            // With TargetWins, losing the baseline would turn the left-side edit into a conflict that the target wins.
            _fs.AddFile(@"C:\left\x.txt", T1, "base");
            await Run(Item(conflict: ConflictResolution.TargetWins));
            _fs.AddFile(@"C:\left\x.txt", T3, "left-edit");
            _fs.DeleteShouldFail = p => FakeFsPath.Comparer.Equals(p, @"C:\right\x.txt"); // separator-agnostic (CI runs on Linux)

            var failed = await Run(Item(conflict: ConflictResolution.TargetWins));
            _fs.DeleteShouldFail = null;
            var retried = await Run(Item(conflict: ConflictResolution.TargetWins));

            failed.Errors.Should().Be(1);
            _fs.ContentOf(@"C:\left\x.txt").Should().Be("left-edit");
            _fs.ContentOf(@"C:\right\x.txt").Should().Be("left-edit");
            retried.Updated.Should().Be(1);
        }

        [Test]
        public async Task SubFolderListingFailure_KeepsItsBaseline_SoADeletionIsPropagatedNotUndone()
        {
            _fs.AddFile(@"C:\left\sub\x.txt", T1, "x");
            await Run(Item());
            _fs.RemoveDirectory(@"C:\right\sub");                                        // deleted on the target
            _fs.GetDirectoriesShouldFail = d => FakeFsPath.Comparer.Equals(d, Left);     // and the source can't list folders

            var failed = await Run(Item(propagateDeletions: true));
            _fs.GetDirectoriesShouldFail = null;
            var next = await Run(Item(propagateDeletions: true));

            failed.Errors.Should().Be(1);
            _fs.FileExists(@"C:\right\sub\x.txt").Should().BeFalse(); // not resurrected as "new on the source"
            _fs.FileExists(@"C:\left\sub\x.txt").Should().BeFalse();  // the deletion is carried through
            next.Deleted.Should().Be(1);
        }

        [Test]
        public async Task ShortRead_IsNotCommitted()
        {
            _fs.AddFile(@"C:\left\a.txt", T1, "full-content");
            _fs.OpenReadOverride = _ => new MemoryStream(Encoding.UTF8.GetBytes("full"), writable: false);

            var result = await Run(Item());

            _fs.FileExists(@"C:\right\a.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Errors.Should().Be(1);
        }

        // ---- Temp files ----

        [Test]
        public async Task UserDotTmpFiles_AreSyncedLikeAnyOtherFile_NotSweptAway()
        {
            // Both sides are live folders: another app's ".x.tmp" (or the user's own) isn't ours to delete.
            _fs.AddFile(@"C:\left\.cache.tmp", T1, "c");

            await Run(Item());

            _fs.FileExists(@"C:\left\.cache.tmp").Should().BeTrue();
            _fs.ContentOf(@"C:\right\.cache.tmp").Should().Be("c");
        }

        [Test]
        public async Task LeftoverTempFromAnInterruptedRun_IsSwept_AndNotSynced()
        {
            _fs.AddFile(@"C:\right\.a.txt.backupservice.tmp", T1, "partial");

            await Run(Item());

            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".backupservice.tmp"));
        }

        [Test]
        public async Task AReadOnlySide_IsRefusedUpFront_InsteadOfFailingEveryCopyEveryRun()
        {
            // An MTP phone as a two-way source: nothing can be written back to it.
            _fs.AddFile(@"C:\right\b.txt", T1, "world");
            _fs.IsReadOnly = true;

            var result = await Run(Item());

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("read-only"));
            _fs.FileExists(@"C:\left\b.txt").Should().BeFalse();
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

            public bool IsReadOnly { get; set; }

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

            public IReadOnlyList<string> AllFiles => _files.Keys.Select(FakeFsPath.Norm).ToList();

            // Removes a folder and everything in it (e.g. a drive being unplugged or a folder renamed away).
            public void RemoveDirectory(string path)
            {
                foreach (var f in _files.Keys.Where(f => FakeFsPath.IsUnder(f, path) || FakeFsPath.Comparer.Equals(FakeFsPath.Parent(f), path)).ToList())
                {
                    _files.Remove(f);
                }
                foreach (var d in _dirs.Where(d => FakeFsPath.Comparer.Equals(d, path) || FakeFsPath.IsUnder(d, path)).ToList())
                {
                    _dirs.Remove(d);
                }
            }

            // Failure injection.
            public Func<string, bool>? DeleteShouldFail { get; set; }          // arg: path
            public Func<string, bool>? GetDirectoriesShouldFail { get; set; } // arg: directory
            public Func<string, Stream>? OpenReadOverride { get; set; }       // arg: path

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

            public IReadOnlyList<string> GetDirectories(string directory)
            {
                if (GetDirectoriesShouldFail?.Invoke(directory) == true)
                {
                    throw new IOException($"Listing failed: {directory}");
                }
                return _dirs.Where(d => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(d), directory))
                    .Select(FakeFsPath.Norm)
                    .ToList();
            }

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

            // Files that exist but can't be downloaded (e.g. a Google Docs file on Drive).
            public HashSet<string> NotDownloadable { get; } = new(FakeFsPath.Comparer);

            public Stream OpenRead(string path)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                if (NotDownloadable.Contains(path))
                {
                    throw new FileNotDownloadableException($"'{path}' can't be downloaded.", "it's a Google Docs file");
                }
                if (OpenReadOverride is not null)
                {
                    return OpenReadOverride(path);
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

            // Files the filesystem refuses to delete or overwrite (e.g. a Google Docs file on Drive).
            public HashSet<string> Protected { get; } = new(FakeFsPath.Comparer);

            public void DeleteFile(string path)
            {
                if (Protected.Contains(path))
                {
                    throw new ProtectedFileException($"'{path}' is protected.", "it's a Google Docs file");
                }
                if (DeleteShouldFail?.Invoke(path) == true)
                {
                    throw new IOException($"Delete failed: {path}");
                }
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
