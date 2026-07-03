using BackupService.Enumerations;
using BackupService.Logging;
using FluentAssertions;

namespace BackupService.UnitTests.Logging
{
    [TestFixture]
    public class OperationLogFileStoreTests
    {
        private TempLogStore _temp = null!;
        private OperationLogFileStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            _temp = new TempLogStore();
            _store = _temp.Store;
        }

        [TearDown]
        public void TearDown() => _temp.Dispose();

        [Test]
        public async Task Append_ThenRead_RoundTripsMessagesLevelsAndOrder()
        {
            await _store.AppendAsync(1, OperationLogLevel.Info, ["first", "second"]);
            await _store.AppendAsync(1, OperationLogLevel.Warning, ["careful"]);
            await _store.AppendAsync(1, OperationLogLevel.Error, ["boom"]);

            var lines = await _store.ReadAsync(1);

            lines.Select(l => l.Message).Should().Equal("first", "second", "careful", "boom");
            lines.Select(l => l.Level).Should().Equal(
                OperationLogLevel.Info, OperationLogLevel.Info, OperationLogLevel.Warning, OperationLogLevel.Error);
        }

        [Test]
        public async Task Read_MissingFile_ReturnsEmpty()
        {
            (await _store.ReadAsync(999)).Should().BeEmpty();
        }

        [Test]
        public async Task ReadTail_ReturnsOnlyTheLastNLines_InOrder()
        {
            for (var i = 1; i <= 20; i++)
            {
                await _store.AppendAsync(21, OperationLogLevel.Info, [$"line {i}"]);
            }

            var tail = await _store.ReadTailAsync(21, 5);

            tail.Select(l => l.Message).Should().Equal("line 16", "line 17", "line 18", "line 19", "line 20");
        }

        [Test]
        public async Task ReadTail_WhenFileHasFewerLinesThanMax_ReturnsAll()
        {
            await _store.AppendAsync(23, OperationLogLevel.Info, ["a", "b"]);

            (await _store.ReadTailAsync(23, 500)).Select(l => l.Message).Should().Equal("a", "b");
        }

        [Test]
        public async Task ReadTail_KeepsMultiLineMessagesIntact()
        {
            await _store.AppendAsync(25, OperationLogLevel.Info, ["first"]);
            await _store.AppendAsync(25, OperationLogLevel.Debug, ["Archived 2 file(s):\r\na.jpg\r\nb.jpg"]);

            var tail = await _store.ReadTailAsync(25, 1);

            // The last logical line is the whole multi-line message, reconstructed (not split by the cap).
            tail.Should().HaveCount(1);
            tail[0].Message.Should().Be("Archived 2 file(s):\na.jpg\nb.jpg");
        }

        [Test]
        public async Task Append_MessageWithEmbeddedNewlines_IsReconstructedAsOneLine()
        {
            // e.g. the ArchiveSync "Archived N file(s):" consolidated multi-line entry.
            var message = "Archived 3 file(s):\r\na.jpg\r\nb.jpg\r\nc.jpg";
            await _store.AppendAsync(5, OperationLogLevel.Debug, [message]);
            await _store.AppendAsync(5, OperationLogLevel.Info, ["done"]);

            var lines = await _store.ReadAsync(5);

            lines.Should().HaveCount(2);
            lines[0].Level.Should().Be(OperationLogLevel.Debug);
            // The physical continuation lines are rejoined (with '\n') into the one logical message.
            lines[0].Message.Should().Be("Archived 3 file(s):\na.jpg\nb.jpg\nc.jpg");
            lines[1].Message.Should().Be("done");
        }

        [Test]
        public async Task LineFormat_KeepsParseablePrefix()
        {
            await _store.AppendAsync(7, OperationLogLevel.Info, ["hello"]);

            var path = Path.Combine(_store.RootDirectory, _store.FileNameFor(7));
            var text = await File.ReadAllTextAsync(path);

            // [Info][yyyy-MM-dd HH:mm:ss] hello
            text.Should().MatchRegex(@"^\[Info\]\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] hello");
        }

        [Test]
        public async Task Contains_MatchesLineTextCaseInsensitively()
        {
            await _store.AppendAsync(9, OperationLogLevel.Info, [@"Source: C:\Backups\Photos"]);

            (await _store.ContainsAsync(9, "backups")).Should().BeTrue();
            (await _store.ContainsAsync(9, "nowhere")).Should().BeFalse();
        }

        [Test]
        public async Task Append_SucceedsWhileFileIsOpenForReading()
        {
            // Regression: a live tail (the View Progress dialog / Logs terminal) holds the log file open for
            // reading while a running backup appends to it. Both sides must share the file — otherwise the
            // append throws "being used by another process" and fails the backup.
            await _store.AppendAsync(13, OperationLogLevel.Info, ["first"]);
            var path = Path.Combine(_store.RootDirectory, _store.FileNameFor(13));

            // A reader holds the file open the way the store's own read path does (share read/write/delete).
            await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            Func<Task> append = () => _store.AppendAsync(13, OperationLogLevel.Info, ["second"]);
            await append.Should().NotThrowAsync();

            (await _store.ReadAsync(13)).Select(l => l.Message).Should().Equal("first", "second");
        }

        [Test]
        public async Task ReadWindow_Tail_ReturnsLastLinesWithTotalsAndLevelCounts()
        {
            for (var i = 1; i <= 10; i++)
            {
                await _store.AppendAsync(31, i % 4 == 0 ? OperationLogLevel.Warning : OperationLogLevel.Info, [$"line {i}"]);
            }
            await _store.AppendAsync(31, OperationLogLevel.Error, ["boom"]);

            var window = await _store.ReadWindowAsync(31, skip: null, take: 3);

            window.Lines.Select(l => l.Message).Should().Equal("line 9", "line 10", "boom");
            window.StartIndex.Should().Be(8);
            window.TotalCount.Should().Be(11);
            window.WarningCount.Should().Be(2); // lines 4 and 8
            window.ErrorCount.Should().Be(1);
            window.HasEarlier.Should().BeTrue();
        }

        [Test]
        public async Task ReadWindow_WithSkip_ReturnsTheRequestedRange()
        {
            for (var i = 1; i <= 10; i++)
            {
                await _store.AppendAsync(33, OperationLogLevel.Info, [$"line {i}"]);
            }

            var window = await _store.ReadWindowAsync(33, skip: 2, take: 3);

            window.Lines.Select(l => l.Message).Should().Equal("line 3", "line 4", "line 5");
            window.StartIndex.Should().Be(2);
            window.TotalCount.Should().Be(10);
        }

        [Test]
        public async Task ReadWindow_WholeFileFitsInTake_StartsAtZero()
        {
            await _store.AppendAsync(35, OperationLogLevel.Info, ["a", "b"]);

            var window = await _store.ReadWindowAsync(35, skip: null, take: 500);

            window.Lines.Should().HaveCount(2);
            window.StartIndex.Should().Be(0);
            window.HasEarlier.Should().BeFalse();
        }

        [Test]
        public async Task ReadWindow_MissingFile_ReturnsEmpty()
        {
            var window = await _store.ReadWindowAsync(999, skip: null, take: 10);

            window.Lines.Should().BeEmpty();
            window.TotalCount.Should().Be(0);
        }

        [Test]
        public async Task Search_MatchesTextCaseInsensitively_InFileOrder()
        {
            await _store.AppendAsync(41, OperationLogLevel.Info, ["Copied 'a.txt'", "skipped", "Copied 'b.txt'"]);

            var result = await _store.SearchAsync(41, "copied", levels: null, maxMatches: 10);

            result.Matches.Select(l => l.Message).Should().Equal("Copied 'a.txt'", "Copied 'b.txt'");
            result.TotalMatches.Should().Be(2);
            result.Truncated.Should().BeFalse();
        }

        [Test]
        public async Task Search_FiltersByLevelSet_AndCombinesWithText()
        {
            await _store.AppendAsync(43, OperationLogLevel.Info, ["copy ok"]);
            await _store.AppendAsync(43, OperationLogLevel.Warning, ["copy locked"]);
            await _store.AppendAsync(43, OperationLogLevel.Error, ["copy failed"]);
            await _store.AppendAsync(43, OperationLogLevel.Error, ["delete failed"]);

            var result = await _store.SearchAsync(43, "copy", [OperationLogLevel.Warning, OperationLogLevel.Error], 10);

            result.Matches.Select(l => l.Message).Should().Equal("copy locked", "copy failed");
            result.TotalMatches.Should().Be(2);
        }

        [Test]
        public async Task Search_CapsReturnedMatches_ButCountsAllOfThem()
        {
            for (var i = 1; i <= 8; i++)
            {
                await _store.AppendAsync(45, OperationLogLevel.Info, [$"match {i}"]);
            }

            var result = await _store.SearchAsync(45, "match", levels: null, maxMatches: 3);

            result.Matches.Select(l => l.Message).Should().Equal("match 1", "match 2", "match 3");
            result.TotalMatches.Should().Be(8);
            result.Truncated.Should().BeTrue();
        }

        [Test]
        public async Task Search_MatchesInsideMultiLineMessages()
        {
            await _store.AppendAsync(47, OperationLogLevel.Debug, ["Archived 2 file(s):\r\nphoto.jpg\r\nnotes.txt"]);
            await _store.AppendAsync(47, OperationLogLevel.Info, ["done"]);

            var result = await _store.SearchAsync(47, "photo.jpg", levels: null, maxMatches: 10);

            result.TotalMatches.Should().Be(1);
            result.Matches[0].Message.Should().Contain("photo.jpg");
        }

        [Test]
        public async Task Delete_RemovesTheFile()
        {
            await _store.AppendAsync(11, OperationLogLevel.Info, ["x"]);
            _store.Delete(11);

            (await _store.ReadAsync(11)).Should().BeEmpty();
        }

        [Test]
        public async Task DeleteAll_RemovesEveryFile()
        {
            await _store.AppendAsync(1, OperationLogLevel.Info, ["a"]);
            await _store.AppendAsync(2, OperationLogLevel.Info, ["b"]);

            _store.DeleteAll();

            (await _store.ReadAsync(1)).Should().BeEmpty();
            (await _store.ReadAsync(2)).Should().BeEmpty();
        }
    }
}
