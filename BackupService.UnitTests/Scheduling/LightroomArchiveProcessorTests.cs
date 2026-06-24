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
    public class LightroomArchiveProcessorTests
    {
        private const string Source = @"C:\src";
        private const string Target = @"C:\dst";
        private const string Lightroom = @"C:\lr";

        private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        private FakeFileSystem _fs = null!;
        private CapturingLogger _log = null!;
        private LightroomArchiveProcessor _sut = null!;

        [SetUp]
        public void SetUp()
        {
            _fs = new FakeFileSystem();
            _fs.AddDirectory(Source);
            _fs.AddDirectory(Target);
            _fs.AddDirectory(Lightroom);
            _log = new CapturingLogger();
            _sut = new LightroomArchiveProcessor(new FakeEndpointFactory(_fs), _fs);
        }

        private static LightroomArchiveItem Item(bool allowDeletions = false) => new()
        {
            Name = "L",
            SourceFolder = Source,
            TargetFolder = Target,
            DebounceMilliseconds = 1000,
            AllowDeletions = allowDeletions,
        };

        private static LightroomArchiveSettings Settings(string rawFormats = ".DNG,.ARW", string rawFolder = "RAW") =>
            new(Lightroom, LightroomArchiveSettings.ParseExtensions(rawFormats), rawFolder);

        private Task<BackupResult> Run(LightroomArchiveItem item, LightroomArchiveSettings settings, string[] changes, string[]? deletes = null, IBackupFileSystem? targetFs = null)
        {
            var sut = targetFs is null ? _sut : new LightroomArchiveProcessor(new FakeEndpointFactory(targetFs), _fs);
            return sut.ProcessBatchAsync(item, settings, changes, deletes ?? [], _log, progress: null, CancellationToken.None);
        }

        [Test]
        public async Task CopiedFile_AlsoCopiesMatchingRaw_IntoRawSubfolderBesideIt()
        {
            _fs.AddFile(@"C:\src\holidays\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\2026\wibble.dng", T1, "raw");

            var result = await Run(Item(), Settings(), [@"C:\src\holidays\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\holidays\wibble.jpg").Should().BeTrue();
            _fs.FileExists(@"C:\dst\holidays\RAW\wibble.dng").Should().BeTrue();
            _fs.ContentOf(@"C:\dst\holidays\RAW\wibble.dng").Should().Be("raw");
            result.Copied.Should().Be(2); // the photo + its raw
            _fs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
        }

        [Test]
        public async Task MultipleRawMatches_FromAnywhereInTree_AreAllCopied()
        {
            _fs.AddFile(@"C:\src\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\a\wibble.dng", T1, "d");
            _fs.AddFile(@"C:\lr\b\wibble.arw", T1, "a");

            var result = await Run(Item(), Settings(), [@"C:\src\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\RAW\wibble.dng").Should().BeTrue();
            _fs.FileExists(@"C:\dst\RAW\wibble.arw").Should().BeTrue();
            result.Copied.Should().Be(3); // jpg + two raws
        }

        [Test]
        public async Task MatchingIsCaseInsensitive_OnNameAndExtension()
        {
            _fs.AddFile(@"C:\src\WIBBLE.JPG", T1, "jpg");
            _fs.AddFile(@"C:\lr\wibble.Dng", T1, "raw");

            await Run(Item(), Settings(), [@"C:\src\WIBBLE.JPG"]);

            _fs.FileExists(@"C:\dst\RAW\wibble.Dng").Should().BeTrue();
        }

        [Test]
        public async Task NoMatchingRaw_OnlyTheFileIsCopied_NoRawFolderCreated()
        {
            _fs.AddFile(@"C:\src\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\other.dng", T1, "raw"); // different basename

            var result = await Run(Item(), Settings(), [@"C:\src\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\wibble.jpg").Should().BeTrue();
            _fs.DirectoryExists(@"C:\dst\RAW").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task RawExtensionNotInList_IsIgnored()
        {
            _fs.AddFile(@"C:\src\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\wibble.cr2", T1, "raw"); // .CR2 not configured

            var result = await Run(Item(), Settings(".DNG,.ARW"), [@"C:\src\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\RAW\wibble.cr2").Should().BeFalse();
            result.Copied.Should().Be(1);
        }

        [Test]
        public async Task UnchangedFileAndRaw_AreSkippedOnReRun()
        {
            _fs.AddFile(@"C:\src\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\wibble.dng", T1, "raw");

            (await Run(Item(), Settings(), [@"C:\src\wibble.jpg"])).Copied.Should().Be(2);

            // Second pass: nothing changed (timestamps match), so nothing is re-copied.
            var second = await Run(Item(), Settings(), [@"C:\src\wibble.jpg"]);
            second.Copied.Should().Be(0);
            second.Updated.Should().Be(0);
            second.Errors.Should().Be(0);
        }

        [Test]
        public async Task DeletedSource_WithAllowDeletions_RemovesFileAndMatchingRaws_LeavingOthers()
        {
            _fs.AddFile(@"C:\dst\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\dst\RAW\wibble.dng", T1, "d");
            _fs.AddFile(@"C:\dst\RAW\wibble.arw", T1, "a");
            _fs.AddFile(@"C:\dst\RAW\other.dng", T1, "o"); // different basename — kept

            var result = await Run(Item(allowDeletions: true), Settings(), changes: [], deletes: [@"C:\src\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\wibble.jpg").Should().BeFalse();
            _fs.FileExists(@"C:\dst\RAW\wibble.dng").Should().BeFalse();
            _fs.FileExists(@"C:\dst\RAW\wibble.arw").Should().BeFalse();
            _fs.FileExists(@"C:\dst\RAW\other.dng").Should().BeTrue();
            result.Deleted.Should().Be(3); // the jpg + its two raws
        }

        [Test]
        public async Task DeletedSource_WithoutAllowDeletions_LeavesEverything()
        {
            _fs.AddFile(@"C:\dst\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\dst\RAW\wibble.dng", T1, "d");

            var result = await Run(Item(allowDeletions: false), Settings(), changes: [], deletes: [@"C:\src\wibble.jpg"]);

            _fs.FileExists(@"C:\dst\wibble.jpg").Should().BeTrue();
            _fs.FileExists(@"C:\dst\RAW\wibble.dng").Should().BeTrue();
            result.Deleted.Should().Be(0);
        }

        [Test]
        public async Task RemoteTarget_IsWrittenThroughTheResolvedEndpoint_NotTheLocalFilesystem()
        {
            _fs.AddFile(@"C:\src\wibble.jpg", T1, "jpg");
            _fs.AddFile(@"C:\lr\wibble.dng", T1, "raw");

            var targetFs = new FakeFileSystem();
            targetFs.AddDirectory(Target);

            var result = await Run(Item(), Settings(), [@"C:\src\wibble.jpg"], targetFs: targetFs);

            // Written to the (separate) target filesystem, not the local one.
            _fs.FileExists(@"C:\dst\wibble.jpg").Should().BeFalse();
            targetFs.FileExists(@"C:\dst\wibble.jpg").Should().BeTrue();
            targetFs.FileExists(@"C:\dst\RAW\wibble.dng").Should().BeTrue();
            result.Copied.Should().Be(2);
        }

        [Test]
        public async Task LightroomIndex_IsBuiltOncePerBatch_NotPerFile()
        {
            // Two changed files in one batch; the Lightroom tree (root + two sub-folders) should be walked once.
            _fs.AddFile(@"C:\src\one.jpg", T1, "1");
            _fs.AddFile(@"C:\src\two.jpg", T1, "2");
            _fs.AddFile(@"C:\lr\a\one.dng", T1, "d1");
            _fs.AddFile(@"C:\lr\b\two.dng", T1, "d2");

            // Separate target fs so local GetFiles calls only reflect the Lightroom index walk.
            var targetFs = new FakeFileSystem();
            targetFs.AddDirectory(Target);

            await Run(Item(), Settings(), [@"C:\src\one.jpg", @"C:\src\two.jpg"], targetFs: targetFs);

            // lr + lr\a + lr\b = 3 directories enumerated exactly once for the index (independent of file count).
            _fs.GetFilesCount(Lightroom).Should().Be(1);
            _fs.GetFilesCount(@"C:\lr\a").Should().Be(1);
            _fs.GetFilesCount(@"C:\lr\b").Should().Be(1);
        }

        // ---- Fakes ----

        private sealed class FakeEndpointFactory(IBackupFileSystem targetFs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default) =>
                Task.FromResult(new EndpointFileSystem(targetFs, configuredPath, NoopDisposable.Instance));
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
            private readonly Dictionary<string, int> _getFilesCounts = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<string> AllFiles => _files.Keys.ToList();

            public int GetFilesCount(string directory) => _getFilesCounts.GetValueOrDefault(directory);

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
                _getFilesCounts[directory] = _getFilesCounts.GetValueOrDefault(directory) + 1;
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
                if (_files.TryGetValue(path, out var e))
                {
                    _files[path] = e with { Time = value };
                }
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

            public void CopyFile(string source, string destination, bool overwrite)
            {
                if (!_files.TryGetValue(source, out var e))
                {
                    throw new FileNotFoundException(source);
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

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true) =>
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

            private static bool IsUnder(string path, string directory)
            {
                var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
