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
