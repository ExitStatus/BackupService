using System.Text;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Scheduling;
using BackupService.UnitTests.Connections;
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
            _sut = new ArchiveSyncProcessor(_fs, new LocalEndpointFactory(_fs), new ReversibleProtector());
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

            var result = await _sut.CreateArchiveAsync(KeepLastN(5), null, null, 1, RunTime, _log, CancellationToken.None);

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
        public async Task ReportsProgress_FilesZippedTo75ThenBytesCopiedTo100()
        {
            _fs.AddFile(@"C:\src\a.txt", RunTime, "data");
            _fs.AddFile(@"C:\src\b.txt", RunTime, "data");
            _fs.AddFile(@"C:\src\c.txt", RunTime, "data");
            _fs.AddFile(@"C:\src\d.txt", RunTime, "data");

            var reports = new List<double>();
            var progress = new SyncProgress(reports.Add);

            var result = await _sut.CreateArchiveAsync(KeepLastN(5), null, null, 1, RunTime, _log, CancellationToken.None, progress);

            result.Copied.Should().Be(1);
            reports.Should().NotBeEmpty();
            reports.Should().BeInAscendingOrder();                                 // monotonic
            reports.Should().Contain(v => v > 0 && v < 0.75);                       // intermediate zip progress
            reports.Should().Contain(v => Math.Abs(v - 0.75) < 1e-9);              // end of the zip phase (all files added)
            reports.Max().Should().BeApproximately(1.0, 1e-9);                      // bytes copied takes it to 100%
        }

        private sealed class SyncProgress(Action<double> report) : IProgress<double>
        {
            public void Report(double value) => report(value);
        }

        [Test]
        public async Task RemoteTarget_WritesArchiveToTheTargetFilesystem_NotLocal()
        {
            // Local source/build filesystem, separate remote target filesystem.
            var localFs = new FakeFileSystem();
            localFs.AddDirectory(Source);
            localFs.AddFile(@"C:\src\file.txt", RunTime, "data");

            var remoteFs = new FakeFileSystem();
            var sut = new ArchiveSyncProcessor(localFs, new TwoFsArchiveFactory(localFs, targetConnectionId: 7, remoteFs), new ReversibleProtector());

            var item = new ArchiveSyncItem
            {
                Name = "A",
                SourceFolder = Source,
                TargetFolder = @"archives",
                FileName = "Backup",
                RetentionMode = ArchiveRetentionMode.KeepLastN,
                RetentionCount = 5,
                MaxLevels = 1,
            };

            var result = await sut.CreateArchiveAsync(item, null, 7, 1, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            result.Errors.Should().Be(0);
            var archive = $@"archives\Backup_{RunTime:yyyy-MM-dd_HHmmss}.zip";
            remoteFs.FileExists(archive).Should().BeTrue();              // landed on the remote target
            remoteFs.AllFiles.Should().NotContain(p => p.EndsWith(".tmp"));
            localFs.AllFiles.Should().NotContain(p => p.EndsWith(".zip")); // local temp build was cleaned up
        }

        [Test]
        public async Task VerboseLogsFiles_AsSingleRow_CrlfSeparated_IncludingSubFolders()
        {
            _fs.AddFile(@"C:\src\top.txt", RunTime, "a");
            _fs.AddFile(@"C:\src\sub\nested.txt", RunTime, "b");
            var item = KeepLastN(5);
            item.IncludeSubFolders = true;

            await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            // One detail row, the file list split across CRLF-separated lines.
            var detail = _log.DebugMessages.Should().ContainSingle().Subject;
            detail.Should().Contain("Archived 2 file(s):");
            detail.Should().Contain("top.txt");
            detail.Should().Contain("sub/nested.txt");
            detail.Should().Contain("\r\n");
        }

        [Test]
        public async Task SkippedFiles_AreLoggedAsWarnings_AndCountAsWarnings_ArchiveStillCreated()
        {
            _fs.AddFile(@"C:\src\readable.txt", RunTime, "ok");
            _fs.SkippedFiles =
            [
                new ZipSkippedFile("locked.vsidx", "being used by another process"),
                new ZipSkippedFile("sub/also-locked.dat", "being used by another process"),
            ];

            var result = await _sut.CreateArchiveAsync(KeepLastN(5), null, null, 1, RunTime, _log, CancellationToken.None);

            // The archive is still produced from the readable files.
            result.Copied.Should().Be(1);
            _fs.FileExists(KeepName(RunTime)).Should().BeTrue();

            // Each skipped (locked/unreadable) file is a non-fatal Warning — counted as a warning, not an error.
            result.Warnings.Should().Be(2);
            result.Errors.Should().Be(0);
            _log.Warnings.Should().HaveCount(2);
            _log.Warnings.Should().Contain(m => m.Contains("Skipped file 'locked.vsidx'") && m.Contains("being used by another process"));
            _log.Warnings.Should().Contain(m => m.Contains("sub/also-locked.dat"));
        }

        [Test]
        public async Task SourceMissing_ReportsError_NoArchive()
        {
            var item = KeepLastN(5);
            item.SourceFolder = @"C:\does-not-exist";

            var result = await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

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

            var result = await _sut.CreateArchiveAsync(KeepLastN(3), null, null, 4, RunTime, _log, CancellationToken.None);

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

            var result = await _sut.CreateArchiveAsync(Gfs(3, 3), null, null, 3, RunTime, _log, CancellationToken.None);

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

            var result = await _sut.CreateArchiveAsync(Gfs(3, 3), null, null, 4, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            result.Deleted.Should().Be(1);
            _fs.FileExists(GfsName(1, s1)).Should().BeFalse(); // oldest son deleted
            _fs.FileExists(GfsName(2, s1)).Should().BeFalse(); // not promoted
            _fs.FileExists(GfsName(1, RunTime)).Should().BeTrue();
        }

        [Test]
        public async Task IncludeExcludeFilters_RestrictWhichFilesAreArchived()
        {
            _fs.AddFile(@"C:\src\keep.txt", RunTime, "a");
            _fs.AddFile(@"C:\src\notes.txt", RunTime, "b");
            _fs.AddFile(@"C:\src\secret.txt", RunTime, "c");
            _fs.AddFile(@"C:\src\image.png", RunTime, "d");
            var item = KeepLastN(5);
            item.Filters.Add(new ArchiveSyncFilter { Direction = FilterDirection.Include, Kind = FilterKind.File, Pattern = "*.txt" });
            item.Filters.Add(new ArchiveSyncFilter { Direction = FilterDirection.Exclude, Kind = FilterKind.File, Pattern = "secret.txt" });

            await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            // Only *.txt minus secret.txt — image.png excluded by the include filter, secret.txt by the exclude.
            var detail = _log.DebugMessages.Should().ContainSingle().Subject;
            detail.Should().Contain("Archived 2 file(s):");
            detail.Should().Contain("keep.txt");
            detail.Should().Contain("notes.txt");
            detail.Should().NotContain("secret.txt");
            detail.Should().NotContain("image.png");
        }

        [Test]
        public async Task OnlyCopyOnChange_FirstRun_CreatesArchive_WithFingerprintComment()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.OnlyCopyOnChange = true;

            var result = await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            _fs.GetZipComment(KeepName(RunTime)).Should().NotBeNullOrEmpty(); // fingerprint stored for next time
        }

        [Test]
        public async Task OnlyCopyOnChange_SecondRunUnchanged_SkipsArchive()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.OnlyCopyOnChange = true;

            await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);
            var zipsAfterFirst = _fs.AllFiles.Count(p => p.EndsWith(".zip"));

            var second = await _sut.CreateArchiveAsync(item, null, null, 2, RunTime.AddHours(1), _log, CancellationToken.None);

            second.Copied.Should().Be(0);
            second.Deleted.Should().Be(0);
            _fs.AllFiles.Count(p => p.EndsWith(".zip")).Should().Be(zipsAfterFirst); // no new archive created
            _log.Messages.Should().Contain(m => m.Contains("unchanged") && m.Contains("skipping"));
        }

        [Test]
        public async Task OnlyCopyOnChange_AfterSourceChanges_CreatesNewArchive()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.OnlyCopyOnChange = true;

            await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            _fs.AddFile(@"C:\src\file.txt", RunTime, "CHANGED"); // same path, different content
            var second = await _sut.CreateArchiveAsync(item, null, null, 2, RunTime.AddHours(1), _log, CancellationToken.None);

            second.Copied.Should().Be(1);
            _fs.FileExists(KeepName(RunTime)).Should().BeTrue();
            _fs.FileExists(KeepName(RunTime.AddHours(1))).Should().BeTrue();
        }

        [Test]
        public async Task UsesConfiguredCompressionLevel()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.CompressionLevel = ArchiveCompressionLevel.SmallestSize;

            await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            _fs.LastCompressionLevel.Should().Be(System.IO.Compression.CompressionLevel.SmallestSize);
        }

        [Test]
        public async Task PasswordProtected_DecryptsPassword_AndPassesEncryptionMethod()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.PasswordProtect = true;
            item.PasswordEncrypted = new ReversibleProtector().Protect("hunter2");
            item.EncryptionMethod = ArchiveEncryptionMethod.ZipCrypto;

            var result = await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            result.Copied.Should().Be(1);
            _fs.LastPassword.Should().Be("hunter2");      // decrypted before zipping
            _fs.LastUseAesEncryption.Should().BeFalse();  // ZipCrypto chosen
        }

        [Test]
        public async Task PasswordProtected_WithNoStoredPassword_Errors_NoArchive()
        {
            _fs.AddFile(@"C:\src\file.txt", RunTime, "data");
            var item = KeepLastN(5);
            item.PasswordProtect = true;
            item.PasswordEncrypted = null;

            var result = await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            result.Errors.Should().Be(1);
            result.Copied.Should().Be(0);
            _fs.FileExists(KeepName(RunTime)).Should().BeFalse();
        }

        [Test]
        public async Task OnlyCopyOnChange_UnreadableSourceFile_DoesNotFailArchive()
        {
            // A cloud-only file that can't be read must not abort the fingerprint pass (and hence the whole
            // archive) — it's skipped from the manifest, the readable files are still archived.
            _fs.AddFile(@"C:\src\readable.txt", RunTime, "data");
            _fs.AddFile(@"C:\src\cloud.txt", RunTime, "data");
            _fs.Unreadable.Add(@"C:\src\cloud.txt");
            var item = KeepLastN(5);
            item.OnlyCopyOnChange = true;

            var result = await _sut.CreateArchiveAsync(item, null, null, 1, RunTime, _log, CancellationToken.None);

            result.Errors.Should().Be(0);
            result.Copied.Should().Be(1);
            _fs.FileExists(KeepName(RunTime)).Should().BeTrue();
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

        // Resolves every endpoint to the one fake filesystem, starting at the configured path (so local
        // source/target both map to the fake — matching the pre-connection behaviour the tests assert).
        private sealed class LocalEndpointFactory(IBackupFileSystem fs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default) =>
                Task.FromResult(new EndpointFileSystem(fs, configuredPath, NoopDisposable.Instance));
        }

        // Returns the remote filesystem for the target connection id, the local one otherwise.
        private sealed class TwoFsArchiveFactory(IBackupFileSystem localFs, int targetConnectionId, IBackupFileSystem remoteFs) : IEndpointFileSystemFactory
        {
            public Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default)
            {
                var fs = connectionId == targetConnectionId ? remoteFs : localFs;
                return Task.FromResult(new EndpointFileSystem(fs, configuredPath, NoopDisposable.Instance));
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
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

            public long GetFileSize(string path) =>
                _files.TryGetValue(path, out var e) ? e.Content.Length : throw new FileNotFoundException(path);

            public void SetLastWriteTimeUtc(string path, DateTime value)
            {
                if (_files.TryGetValue(path, out var e))
                {
                    _files[path] = e with { Time = value };
                }
            }

            // Files that exist (are enumerated) but throw on read — e.g. a OneDrive cloud-only placeholder
            // that can't hydrate from a session-0 service.
            public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

            public Stream OpenRead(string path)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    throw new FileNotFoundException(path);
                }
                if (Unreadable.Contains(path))
                {
                    throw new IOException("Access to the cloud file is denied.");
                }
                return new MemoryStream(Encoding.UTF8.GetBytes(e.Content), writable: false);
            }

            public Stream OpenWrite(string path) =>
                new FakeWriteStream(bytes => _files[path] = new Entry(default, Encoding.UTF8.GetString(bytes)));

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

            // Files the next CreateZipFromDirectory call reports as skipped (locked/unreadable).
            public IReadOnlyList<ZipSkippedFile> SkippedFiles { get; set; } = [];

            // The compression level / encryption args passed to the most recent CreateZipFromDirectory call.
            public System.IO.Compression.CompressionLevel LastCompressionLevel { get; private set; }
            public string? LastPassword { get; private set; }
            public bool LastUseAesEncryption { get; private set; }

            public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null)
            {
                LastCompressionLevel = compressionLevel;
                LastPassword = password;
                LastUseAesEncryption = useAesEncryption;
                // Stand in for a real zip — record a file at the destination so the copy can read it,
                // and return the relative entry names of the source files (top-level or recursive),
                // honouring the include/exclude predicate so filtering is exercised. The archive comment is
                // embedded in the "content" so it survives the stream copy to the target (like a real EOCD).
                var entries = _files.Keys
                    .Where(f => includeSubfolders
                        ? IsUnder(f, sourceDirectory)
                        : string.Equals(Path.GetDirectoryName(f), sourceDirectory, StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(sourceDirectory, f).Replace('\\', '/'))
                    .Where(e => includeEntry is null || includeEntry(e))
                    .ToList();
                AddFile(destinationZip, DateTime.UtcNow, comment is null ? "zip" : "zip " + comment);
                foreach (var entry in entries)
                {
                    onEntryProcessed?.Invoke(entry); // drive the per-file progress callback
                }
                return new ZipBuildResult(entries, SkippedFiles);
            }

            public string? GetZipComment(string path)
            {
                if (!_files.TryGetValue(path, out var e))
                {
                    return null;
                }
                var marker = e.Content.IndexOf(' ');
                return marker < 0 ? null : e.Content[(marker + 1)..];
            }

            private static bool IsUnder(string path, string directory)
            {
                var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
