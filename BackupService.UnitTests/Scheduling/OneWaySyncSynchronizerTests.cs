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
    public class OneWaySyncSynchronizerTests
    {
        private const string Source = @"C:\src";
        private const string Target = @"C:\dst";

        private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime T2 = new(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc); // newer than T1

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private OneWaySyncSynchronizer _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Source);
            _log = new CapturingLogger();
            _sut = new OneWaySyncSynchronizer(new SingleFsEndpointFactory(_fs));
        }

        private static OneWaySyncItem Pair(
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

        private Task<BackupResult> Run(OneWaySyncItem pair) => _sut.SyncAsync(pair, null, null, _log, CancellationToken.None);

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
        public async Task SourceWithUnstampableTimestamp_StillCopies_WithoutError()
        {
            // An MTP camera that exposes no modified date yields DateTime.MinValue, which is not a valid
            // Win32 FileTime. The copy must still succeed (data is the point), just without stamping the time.
            _fs.AddFile(@"C:\src\a.txt", DateTime.MinValue, "hello");

            var result = await Run(Pair());

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("hello");
            result.Copied.Should().Be(1);
            result.Errors.Should().Be(0);
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
        }

        [Test]
        public async Task SourceEndpointDisconnectedMidCopy_AbortsRun_LeavingNoTemp()
        {
            // A camera switched off mid-copy surfaces as EndpointUnavailableException from the source. The engine
            // must let it propagate (fail fast) rather than swallow it as a per-file error, and leave no temp.
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            _fs.OpenReadOverride = _ => throw new EndpointUnavailableException("device gone");

            Func<Task> act = () => Run(Pair());

            await act.Should().ThrowAsync<EndpointUnavailableException>();
            _fs.FileExists(@"C:\dst\a.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
        }

        [Test]
        public async Task ALinkBackToAFolderAbove_IsNotFollowed()
        {
            // Followed, a junction to an ancestor walks the same tree inside itself without end.
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\src\docs\loop\inside.txt", T1, "would repeat forever");
            _fs.DirectoryLinks[@"C:\src\docs\loop"] = @"C:\src";
            _fs.AddFile(@"C:\src\docs\other\b.txt", T1, "b");
            _fs.DirectoryLinks[@"C:\src\docs\other"] = @"D:\elsewhere"; // a link elsewhere is still followed

            var result = await Run(Pair(includeSubFolders: true));

            _fs.FileExists(@"C:\dst\a.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\docs\other\b.txt").Should().BeTrue();
            _fs.FileExists(@"C:\dst\docs\loop\inside.txt").Should().BeFalse();
            result.Errors.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("link back to"));
        }

        [Test]
        public async Task AReplaceWhoseRenameFails_KeepsTheOldCopy()
        {
            // The old copy used to be deleted before the new one was renamed in, so a failed rename lost both.
            _fs.AddFile(@"C:\src\a.txt", T2, "new");
            _fs.AddFile(@"C:\dst\a.txt", T1, "old");
            _fs.MoveShouldFail = dest => dest.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase);

            var result = await Run(Pair());

            result.Errors.Should().Be(1);
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("old");
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
        }

        [Test]
        public async Task ACopyThatFailsPartWay_AbandonsTheWrite_SoNothingPartialIsUploaded()
        {
            // Google Drive uploads a write on close; without abandoning it, a failed or stopped copy uploaded its
            // partial file first (a Stop took as long as that upload), only for the copy to delete it.
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            _fs.OpenReadOverride = _ => new FailingReadStream();

            var result = await Run(Pair());

            _fs.AbandonedWrites.Should().Be(1);
            result.Errors.Should().Be(1);
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
        }

        private sealed class FailingReadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The network went away.");
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Test]
        public async Task SourceStreamEndsEarly_CopyIsNotCommitted_AndIsAnError()
        {
            // A flaky MTP transfer can end the source stream early without throwing. The truncated data must
            // not be renamed into place (and stamped with the source time, which would hide it from later runs).
            _fs.AddFile(@"C:\src\photo.arw", T1, "full-content");
            _fs.OpenReadOverride = _ => new MemoryStream(Encoding.UTF8.GetBytes("full"), writable: false);

            var result = await Run(Pair());

            _fs.FileExists(@"C:\dst\photo.arw").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Copied.Should().Be(0);
            result.Errors.Should().Be(1);
            _log.Errors.Should().Contain(m => m.Contains("Incomplete copy"));
        }

        [Test]
        public async Task SourceShrinksDuringCopy_IsSkippedAsWarning_NotCommittedOrAnError()
        {
            // The source is truncated while it is being read (e.g. an app saving a smaller version). The short read
            // must not be committed, but it isn't a failed transfer either — skip it as a warning, like a locked file.
            _fs.AddFile(@"C:\src\log.txt", T1, "full-content");
            _fs.OpenReadOverride = path =>
            {
                _fs.SetContent(path, "full");
                return new MemoryStream(Encoding.UTF8.GetBytes("full"), writable: false);
            };

            var result = await Run(Pair());

            _fs.FileExists(@"C:\dst\log.txt").Should().BeFalse();
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Copied.Should().Be(0);
            result.Errors.Should().Be(0);
            result.Warnings.Should().Be(1);
            _log.Warnings.Should().ContainSingle(m => m.Contains("log.txt") && m.Contains("changed while it was being copied"));
        }

        [Test]
        public async Task SourceFileThatCantBeDownloaded_IsSkippedAsWarning_AndOthersStillCopy()
        {
            // A Google Docs file in a Drive source has no downloadable content: skip it with a warning naming the
            // reason, not an error — and never leave an empty copy that looks backed up.
            _fs.AddFile(@"C:\src\Report", T1, "");
            _fs.AddFile(@"C:\src\photo.jpg", T1, "jpeg");
            _fs.OpenReadOverride = p => p.EndsWith("Report")
                ? throw new FileNotDownloadableException("not downloadable", "it's a Google Docs file")
                : new MemoryStream(Encoding.UTF8.GetBytes("jpeg"), writable: false);

            var result = await Run(Pair());

            _fs.FileExists(@"C:\dst\Report").Should().BeFalse();
            _fs.ContentOf(@"C:\dst\photo.jpg").Should().Be("jpeg");
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Copied.Should().Be(1);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _log.Warnings.Should().ContainSingle(m => m.Contains("Report") && m.Contains("Google Docs"));
        }

        [Test]
        public async Task DestinationNewer_UpdateOnlyIfContentMatches_SourceCantBeRead_IsAWarningNotAnError()
        {
            _fs.AddFile(@"C:\src\Report", T1, "");
            _fs.AddFile(@"C:\dst\Report", T2, "");
            _fs.OpenReadOverride = _ => throw new FileNotDownloadableException("not downloadable", "it's a Google Docs file");

            var result = await Run(Pair(overwrite: OverwriteBehaviour.UpdateOnlyIfContentMatches));

            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _fs.TimeOf(@"C:\dst\Report").Should().Be(T2); // untouched
        }

        [Test]
        public async Task EqualTimestamp_DestinationShorter_IsRepaired_AndCountedAsAWarning()
        {
            // A previously truncated copy carries the source's timestamp; the shorter destination must be re-copied,
            // and the warning it logs must be counted so the run summary/outcome agree with the log's level.
            _fs.AddFile(@"C:\src\photo.arw", T1, "full-content");
            _fs.AddFile(@"C:\dst\photo.arw", T1, "full");

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\photo.arw").Should().Be("full-content");
            result.Updated.Should().Be(1);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _log.Warnings.Should().ContainSingle(m => m.Contains("Repaired") && m.Contains("4 bytes") && m.Contains("12 bytes"));
        }

        [Test]
        public async Task EqualTimestamp_DestinationLarger_IsNotRepaired()
        {
            // A destination LARGER than its source isn't a truncated copy (e.g. a tool rewrote the source smaller but
            // kept its timestamp), so it's left alone rather than "repaired" with a misleading warning.
            _fs.AddFile(@"C:\src\a.txt", T1, "short");
            _fs.AddFile(@"C:\dst\a.txt", T1, "much longer content");

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("much longer content");
            result.Updated.Should().Be(0);
            result.Warnings.Should().Be(0);
            _log.Messages.Should().NotContain(m => m.Contains("a.txt"));
        }

        [TestCase(0L)]
        [TestCase(-1L)] // an MTP size of ulong.MaxValue, cast to long
        public async Task SourceReportingNoUsableSize_IsCopiedOnce_ThenLeftAlone(long reportedSize)
        {
            // An MTP device can report 0 (or an overflowing sentinel) for a real, non-empty file. There's nothing to
            // verify against, so the copy is accepted — and later runs must not see the real-sized copy as a size
            // mismatch and re-copy it forever.
            _fs.AddFile(@"C:\src\photo.arw", T1, "full-content");
            _fs.ReportedSizeOverride = p => FakeFsPath.IsUnder(p, Source) ? reportedSize : null;

            var first = await Run(Pair());
            var second = await Run(Pair());

            _fs.ContentOf(@"C:\dst\photo.arw").Should().Be("full-content");
            first.Copied.Should().Be(1);
            first.Errors.Should().Be(0);
            second.Copied.Should().Be(0);
            second.Updated.Should().Be(0);
            second.Warnings.Should().Be(0);
            _log.Messages.Should().NotContain(m => m.Contains("Repaired"));
        }

        [TestCase(OverwriteBehaviour.DoNotOverwriteNewer, false)]
        [TestCase(OverwriteBehaviour.UpdateOnlyIfContentMatches, false)]
        [TestCase(OverwriteBehaviour.AlwaysOverwrite, true)]
        public async Task EqualTimestamp_DestinationShorterButMarginallyNewer_RespectsOverwriteBehaviour(OverwriteBehaviour behaviour, bool repaired)
        {
            // Within the 2s tolerance the destination can still be NEWER — possibly a genuine edit. Replacing a newer
            // destination is the overwrite behaviour's call, so only AlwaysOverwrite may repair it.
            _fs.AddFile(@"C:\src\a.txt", T1, "source-content");
            _fs.AddFile(@"C:\dst\a.txt", T1.AddSeconds(1.5), "edited");

            var result = await Run(Pair(overwrite: behaviour));

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be(repaired ? "source-content" : "edited");
            result.Updated.Should().Be(repaired ? 1 : 0);
            // Either way it's flagged: repaired, or kept despite looking like an incomplete copy.
            result.Warnings.Should().Be(1);
            _log.Warnings.Should().ContainSingle(m => m.Contains(repaired ? "Repaired" : "Kept"));
        }

        [Test]
        public async Task EqualTimestamp_DestinationShorterButOlderWithinTolerance_IsRepairedEvenWhenNotOverwritingNewer()
        {
            // The destination isn't newer, so there's nothing for DoNotOverwriteNewer to protect.
            _fs.AddFile(@"C:\src\a.txt", T1, "source-content");
            _fs.AddFile(@"C:\dst\a.txt", T1.AddSeconds(-1), "short");

            var result = await Run(Pair(overwrite: OverwriteBehaviour.DoNotOverwriteNewer));

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("source-content");
            result.Updated.Should().Be(1);
        }

        [Test]
        public async Task UnchangedFile_ReadsEachSideMetadataInOneLookup()
        {
            // The size check must not add round trips: one combined stat per side, no separate time/size reads.
            _fs.AddFile(@"C:\src\a.txt", T1, "same");
            _fs.AddFile(@"C:\dst\a.txt", T1, "same");

            await Run(Pair());

            _fs.GetFileStatCalls.Should().Be(2);
            _fs.GetLastWriteTimeCalls.Should().Be(0);
            _fs.GetFileSizePaths.Should().BeEmpty();
        }

        [Test]
        public async Task CopiedFile_ReadsSourceMetadataOnce_AndNeverStatsTheTemp()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");

            await Run(Pair());

            _fs.GetFileStatCalls.Should().Be(1);
            _fs.GetLastWriteTimeCalls.Should().Be(0);
            _fs.GetFileSizePaths.Should().BeEmpty();
        }

        [Test]
        public async Task TargetSizeUnreadable_DoesNotDiscardACompletedCopy()
        {
            // A failed size query on the target must never throw away a fully written copy.
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            _fs.GetFileSizeShouldFail = p => FakeFsPath.IsUnder(p, Target);

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("hello");
            result.Copied.Should().Be(1);
            result.Errors.Should().Be(0);
            result.BytesCopied.Should().Be(5);
        }

        [Test]
        public async Task Copy_PassesTheRunTokenToOpenRead()
        {
            // MTP/Drive download the whole file inside OpenRead, so the token must reach it for Stop to interrupt.
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            using var cts = new CancellationTokenSource();

            await _sut.SyncAsync(Pair(), null, null, _log, cts.Token);

            _fs.LastOpenReadToken.Should().Be(cts.Token);
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
        public async Task DestinationSlightlyNewer_WithinFatGranularity_IsTreatedAsUnchanged()
        {
            // A FAT/exFAT USB target rounds a stamped write-time up to 2-second granularity, so the destination
            // reads back slightly newer than the source. The file is unchanged and must not be re-copied — even
            // with AlwaysOverwrite (the reported bug: every unchanged file re-copied "destination was newer").
            _fs.AddFile(@"C:\src\a.txt", T1, "hello");
            _fs.AddFile(@"C:\dst\a.txt", T1.AddSeconds(1.5), "hello");

            var result = await Run(Pair(overwrite: OverwriteBehaviour.AlwaysOverwrite));

            result.Copied.Should().Be(0);
            result.Updated.Should().Be(0);
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
        public async Task OrphanFolder_KeepsFilesTheRulesPutOutOfScope_AndSoTheFolder()
        {
            // Out-of-scope target files are left alone when their folder exists in the source — and must be when it
            // doesn't, too.
            _fs.AddFile(@"C:\dst\Old\notes.txt", T1, "n");
            _fs.AddFile(@"C:\dst\Old\art.psd", T1, "p");
            var pair = Pair(allowDeletions: true, includeSubFolders: true);
            pair.Filters.Add(new OneWaySyncFilter { Direction = FilterDirection.Exclude, Kind = FilterKind.File, Pattern = "*.psd" });

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\Old\notes.txt").Should().BeFalse();
            _fs.FileExists(@"C:\dst\Old\art.psd").Should().BeTrue();
            _fs.DirectoryExists(@"C:\dst\Old").Should().BeTrue();
            result.Errors.Should().Be(0);
        }

        [Test]
        public async Task OrphanFolder_KeepsExcludedSubFolders()
        {
            _fs.AddFile(@"C:\dst\Old\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\Old\bin\tool.dll", T1, "d");
            var pair = Pair(allowDeletions: true, includeSubFolders: true);
            pair.Filters.Add(new OneWaySyncFilter { Direction = FilterDirection.Exclude, Kind = FilterKind.Folder, Pattern = "bin" });

            var result = await Run(pair);

            _fs.FileExists(@"C:\dst\Old\a.txt").Should().BeFalse();
            _fs.FileExists(@"C:\dst\Old\bin\tool.dll").Should().BeTrue();
            result.Errors.Should().Be(0);
        }

        [Test]
        public async Task OrphanFolderLink_RemovesOnlyTheLink_NeverWhatItPointsAt()
        {
            // A junction in the target pointing at D:\Important: deleting "its" files would delete the real ones.
            _fs.AddFile(@"C:\dst\Shared\important.docx", T1, "precious");
            _fs.Links.Add(@"C:\dst\Shared");

            var result = await Run(Pair(allowDeletions: true, includeSubFolders: true));

            _fs.DeletedFiles.Should().BeEmpty();
            _fs.RemovedLinks.Should().ContainSingle();
            result.Errors.Should().Be(0);
            _log.Messages.Should().Contain(m => m.Contains("Deleted folder link"));
        }

        [Test]
        public async Task SourceFileDeletedAfterTheListing_IsSkippedQuietly()
        {
            _fs.AddFile(@"C:\src\temp.txt", T2, "t");
            _fs.AddFile(@"C:\dst\temp.txt", T1, "old");
            _fs.OnGetFileStat = p => { if (FakeFsPath.IsUnder(p, Source)) _fs.Remove(p); };

            var result = await Run(Pair());

            result.Errors.Should().Be(0);
            result.Updated.Should().Be(0);
            _log.Errors.Should().BeEmpty();
        }

        [Test]
        public async Task NewSourceFileDeletedBeforeItsCopy_IsSkippedQuietly()
        {
            _fs.AddFile(@"C:\src\temp.txt", T1, "t");
            _fs.OnGetFileStat = p => _fs.Remove(p);

            var result = await Run(Pair());

            result.Errors.Should().Be(0);
            result.Copied.Should().Be(0);
            _fs.FileExists(@"C:\dst\temp.txt").Should().BeFalse();
        }

        [Test]
        public async Task DestinationDeletedAfterTheListing_IsCopiedAfresh()
        {
            _fs.AddFile(@"C:\src\a.txt", T1, "a");
            _fs.AddFile(@"C:\dst\a.txt", T1, "a");
            _fs.OnGetFileStat = p => { if (FakeFsPath.IsUnder(p, Target)) _fs.Remove(p); };

            var result = await Run(Pair());

            result.Errors.Should().Be(0);
            result.Copied.Should().Be(1);
            _fs.ContentOf(@"C:\dst\a.txt").Should().Be("a");
        }

        [Test]
        public async Task AllowDeletions_ProtectedOrphan_IsKeptAsWarning_NotAnError()
        {
            // A Google Docs file in a Drive target was never written by a backup — it's the user's own, so the
            // target refuses to delete it and the mirror keeps it, as a warning.
            _fs.AddFile(@"C:\dst\Notes", T1, "");
            _fs.Protected.Add(@"C:\dst\Notes");

            var result = await Run(Pair(allowDeletions: true));

            _fs.FileExists(@"C:\dst\Notes").Should().BeTrue();
            result.Deleted.Should().Be(0);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _log.Warnings.Should().ContainSingle(m => m.Contains("Kept") && m.Contains("Notes") && m.Contains("Google Docs"));
        }

        [Test]
        public async Task AllowDeletions_OrphanFolderHoldingAProtectedFile_KeepsTheFileAndTheFolder()
        {
            // On Drive a folder delete takes everything inside with it, so a folder that still holds something the
            // engine kept must be left in place — without an extra "failed to delete folder" error.
            _fs.AddFile(@"C:\dst\gone\stale.txt", T1, "s");
            _fs.AddFile(@"C:\dst\gone\Notes", T1, "");
            _fs.Protected.Add(@"C:\dst\gone\Notes");

            var result = await Run(Pair(allowDeletions: true, includeSubFolders: true));

            _fs.FileExists(@"C:\dst\gone\stale.txt").Should().BeFalse();
            _fs.FileExists(@"C:\dst\gone\Notes").Should().BeTrue();
            _fs.DirectoryExists(@"C:\dst\gone").Should().BeTrue();
            result.Deleted.Should().Be(1);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
        }

        [Test]
        public async Task OrphanFolderWithAFileThatCantBeDeleted_IsLeftInPlace_WithASingleError()
        {
            // The file's failure is the one error; the folder isn't then also reported as "not empty".
            _fs.AddFile(@"C:\dst\gone\stuck.txt", T1, "s");
            _fs.DeleteShouldFail = p => p.EndsWith("stuck.txt");

            var result = await Run(Pair(allowDeletions: true, includeSubFolders: true));

            _fs.DirectoryExists(@"C:\dst\gone").Should().BeTrue();
            result.Errors.Should().Be(1);
            _log.Errors.Should().ContainSingle(m => m.Contains("stuck.txt"));
        }

        [Test]
        public async Task SourceNewer_ProtectedDestination_IsKeptAsWarning_AndTheTempRemoved()
        {
            _fs.AddFile(@"C:\src\Notes", T2, "binary");
            _fs.AddFile(@"C:\dst\Notes", T1, "");
            _fs.Protected.Add(@"C:\dst\Notes");

            var result = await Run(Pair());

            _fs.ContentOf(@"C:\dst\Notes").Should().Be(""); // not replaced
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            result.Updated.Should().Be(0);
            result.Warnings.Should().Be(1);
            result.Errors.Should().Be(0);
            _log.Warnings.Should().ContainSingle(m => m.Contains("Kept") && m.Contains("Notes"));
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

        private static OneWaySyncFilter Filter(FilterDirection direction, FilterKind kind, string pattern) =>
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

            var sut = new OneWaySyncSynchronizer(new TwoFsEndpointFactory(sourceFs, Source, targetFs));

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

            public IReadOnlyList<string> Warnings =>
                _entries.Where(e => e.Level == OperationLogLevel.Warning).Select(e => e.Message).ToList();

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

            private readonly Dictionary<string, Entry> _files = new(FakeFsPath.Comparer);
            private readonly HashSet<string> _dirs = new(FakeFsPath.Comparer);

            // Failure injection: given a path, return true to throw.
            public Func<string, bool>? CopyShouldFail { get; set; }   // arg: destination
            public Func<string, bool>? CopyLockedFail { get; set; }   // arg: destination — throws a sharing violation
            public Func<string, bool>? MoveShouldFail { get; set; }   // arg: destination
            public Func<string, bool>? GetFilesShouldFail { get; set; } // arg: directory
            public Func<string, bool>? GetFileSizeShouldFail { get; set; } // arg: path
            public Func<string, Stream>? OpenReadOverride { get; set; } // arg: path — supply a custom read stream
            public Func<string, long?>? ReportedSizeOverride { get; set; } // arg: path — the size the fs reports (null = real)

            // Call accounting, for asserting how many metadata lookups the engine makes.
            public int GetFileStatCalls { get; private set; }
            public int GetLastWriteTimeCalls { get; private set; }
            public List<string> GetFileSizePaths { get; } = [];
            public CancellationToken? LastOpenReadToken { get; private set; }

            public IReadOnlyList<string> AllFiles => _files.Keys.Select(FakeFsPath.Norm).ToList();

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

            // Replaces a file's content, keeping its timestamp (e.g. a source truncated in place mid-copy).
            public void SetContent(string path, string content) => _files[path] = _files[path] with { Content = content };

            public string ContentOf(string path) => _files[path].Content;

            public DateTime TimeOf(string path) => _files[path].Time;

            public bool DirectoryExists(string path) => _dirs.Contains(path);

            public void CreateDirectory(string path) => AddDirectory(path);

            // Folders that are links (junctions/symlinks) to somewhere else: what's "inside" one lives elsewhere, and
            // deleting the folder removes only the link (as RemoveDirectory does on Windows).
            public HashSet<string> Links { get; } = new(FakeFsPath.Comparer);
            public List<string> RemovedLinks { get; } = [];
            public List<string> DeletedFiles { get; } = [];

            public bool IsDirectoryLink(string path) => Links.Contains(path);

            public void DeleteDirectory(string path, bool recursive)
            {
                if (Links.Remove(path))
                {
                    RemovedLinks.Add(path);
                    RemoveSubtree(path); // the link's view is gone (the real files it showed weren't touched)
                    return;
                }

                var hasChildren = _files.Keys.Any(f => FakeFsPath.IsUnder(f, path)) || _dirs.Any(d => FakeFsPath.IsUnder(d, path));
                if (hasChildren && !recursive)
                {
                    throw new IOException($"Directory not empty: {path}");
                }

                RemoveSubtree(path);
            }

            private void RemoveSubtree(string path)
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
                    .Where(f => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(f), directory))
                    .Select(FakeFsPath.Norm)
                    .ToList();
            }

            public IReadOnlyList<string> GetDirectories(string directory) =>
                _dirs.Where(d => FakeFsPath.Comparer.Equals(FakeFsPath.Parent(d), directory))
                    .Select(FakeFsPath.Norm)
                    .ToList();

            public DateTime GetLastWriteTimeUtc(string path)
            {
                GetLastWriteTimeCalls++;
                return TimeOrThrow(path);
            }

            public long GetFileSize(string path)
            {
                GetFileSizePaths.Add(path);
                if (GetFileSizeShouldFail?.Invoke(path) == true)
                {
                    throw new IOException($"Size unreadable: {path}");
                }
                return SizeOrThrow(path);
            }

            // Runs just before a stat — e.g. to delete the file, as if it vanished after the folder was listed.
            public Action<string>? OnGetFileStat { get; set; }

            public FileStat GetFileStat(string path)
            {
                GetFileStatCalls++;
                OnGetFileStat?.Invoke(path);
                return new FileStat(TimeOrThrow(path), SizeOrThrow(path));
            }

            public void Remove(string path) => _files.Remove(path);

            private DateTime TimeOrThrow(string path) =>
                _files.TryGetValue(path, out var e) ? e.Time : throw new FileNotFoundException(path);

            private long SizeOrThrow(string path) =>
                _files.TryGetValue(path, out var e)
                    ? ReportedSizeOverride?.Invoke(path) ?? e.Content.Length
                    : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                // Mirror File.SetLastWriteTimeUtc: a pre-1601 time is not a valid Win32 FileTime and throws.
                if (value < DateTime.FromFileTimeUtc(0))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Not a valid Win32 FileTime.");
                }
                _files[path] = e with { Time = value };
            }

            public Stream OpenRead(string path, CancellationToken cancellationToken)
            {
                LastOpenReadToken = cancellationToken;
                return OpenRead(path);
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
                return new FakeWriteStream(bytes => _files[path] = new Entry(default, Encoding.UTF8.GetString(bytes)), () => AbandonedWrites++);
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

            /// <summary>How many writes were abandoned (and so never landed) — like Drive, which uploads on close.</summary>
            public int AbandonedWrites { get; private set; }

            /// <summary>Directory links: link path → where it points.</summary>
            public Dictionary<string, string> DirectoryLinks { get; } = new(FakeFsPath.Comparer);

            public string? GetDirectoryLinkTarget(string path) => DirectoryLinks.GetValueOrDefault(path);

            // A write stream that hands the written bytes to a callback on dispose — unless it was abandoned.
            private sealed class FakeWriteStream(Action<byte[]> onClose, Action? onAbandon = null) : MemoryStream, IAbandonableWrite
            {
                private bool _done;

                public void Abandon()
                {
                    if (!_done)
                    {
                        _done = true;
                        onAbandon?.Invoke();
                    }
                }

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
                if (_files.ContainsKey(destination))
                {
                    if (!overwrite)
                    {
                        throw new IOException($"File exists: {destination}");
                    }
                    // Replacing a file is refused wherever deleting it would be (a protected or locked file).
                    if (Protected.Contains(destination))
                    {
                        throw new ProtectedFileException($"'{destination}' is protected.", "it's a Google Docs file");
                    }
                    if (DeleteShouldFail?.Invoke(destination) == true)
                    {
                        throw new IOException($"Replace failed: {destination}");
                    }
                }
                _files[destination] = e;
                _files.Remove(source);
            }

            // Files the filesystem refuses to delete or overwrite (e.g. a Google Docs file on Drive).
            public HashSet<string> Protected { get; } = new(FakeFsPath.Comparer);

            public Func<string, bool>? DeleteShouldFail { get; set; } // arg: path

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
                DeletedFiles.Add(path);
            }

            public bool FilesContentEqual(string a, string b) =>
                _files.TryGetValue(a, out var ea) && _files.TryGetValue(b, out var eb) && ea.Content == eb.Content;

            public string GetTempFilePath(string fileName) => throw new NotSupportedException();

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null) =>
                throw new NotSupportedException();

            public string? GetZipComment(string path) => throw new NotSupportedException();
        }
    }
}
