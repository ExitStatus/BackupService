using System.Globalization;
using System.Net.Http.Headers;
using BackupService.Connections.GoogleDrive;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace BackupService.FileSystem.GoogleDrive
{
    /// <summary>
    /// <see cref="IBackupFileSystem"/> over the Google Drive v3 API. Drive is id-based, but the sync engine
    /// works in backslash-separated name-paths (relative to My Drive root) — so this class resolves each
    /// path to a folder/file id (walking from root, cached per session) and exposes Drive as a tree.
    /// <para>
    /// Created by <see cref="EndpointFileSystemFactory"/> per run; <see cref="Dispose"/> tears the API client
    /// down. <see cref="OpenRead"/> downloads to a self-deleting local temp; <see cref="OpenWrite"/> buffers
    /// to a local temp and uploads on close. The archive-only members
    /// (<see cref="GetTempFilePath"/>/<see cref="CreateZipFromDirectory"/>) are not supported remotely.
    /// </para>
    /// <para>
    /// Drive's <c>modifiedTime</c> is only millisecond-precise, which would make the engine's exact write-time
    /// compare re-copy every run. To avoid that, <see cref="SetLastWriteTimeUtc"/> also stores the source's
    /// exact tick count in a private app property, and <see cref="GetLastWriteTimeUtc"/> reads it back, so a
    /// round-tripped timestamp is exact.
    /// </para>
    /// </summary>
    public sealed class GoogleDriveBackupFileSystem : IBackupFileSystem, IDisposable
    {
        private const string FolderMimeType = "application/vnd.google-apps.folder";
        private const string OctetStream = "application/octet-stream";
        private const string WriteTicksKey = "backupServiceWriteTicks";
        private const string EntryFields = "id,name,size,modifiedTime,mimeType,appProperties";

        private readonly DriveService _drive;
        // Resolved folder/file ids for this session, keyed by normalised name-path (case-insensitive to match
        // the engine's Windows-style name comparisons).
        private readonly Dictionary<string, string> _folderIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DriveEntry> _files = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        private GoogleDriveBackupFileSystem(DriveService drive) => _drive = drive;

        /// <summary>Builds an authenticated client for <paramref name="info"/>.</summary>
        public static GoogleDriveBackupFileSystem Connect(GoogleDriveConnectionInfo info) =>
            new(GoogleDriveServiceFactory.Create(info));

        public bool DirectoryExists(string path) => TryGetFolderId(path, out _);

        public void CreateDirectory(string path)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0)
            {
                return; // My Drive root always exists
            }

            var parentPath = string.Empty;
            var parentId = FolderId(string.Empty);
            foreach (var segment in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var childPath = parentPath.Length == 0 ? segment : $@"{parentPath}\{segment}";
                if (_folderIds.TryGetValue(childPath, out var existing))
                {
                    parentId = existing;
                    parentPath = childPath;
                    continue;
                }

                var child = FindChild(parentId, segment, isFolder: true);
                string id;
                if (child is not null)
                {
                    id = child.Id;
                }
                else
                {
                    var metadata = new DriveFile { Name = segment, MimeType = FolderMimeType, Parents = [parentId] };
                    var create = _drive.Files.Create(metadata);
                    create.Fields = "id";
                    id = create.Execute().Id;
                }

                _folderIds[childPath] = id;
                parentId = id;
                parentPath = childPath;
            }
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            if (!TryGetFolderId(path, out var id))
            {
                return; // already gone
            }

            // Deleting a Drive folder removes its whole subtree; the engine empties it first either way.
            _drive.Files.Delete(id).Execute();
            InvalidateUnder(Normalize(path));
        }

        public bool FileExists(string path)
        {
            var normalized = Normalize(path);
            if (_files.ContainsKey(normalized))
            {
                return true;
            }

            var (parentPath, name) = SplitParent(normalized);
            if (!TryGetFolderId(parentPath, out var parentId))
            {
                return false;
            }

            var file = FindChild(parentId, name, isFolder: false);
            if (file is null)
            {
                return false;
            }

            _files[normalized] = ToEntry(file);
            return true;
        }

        public IReadOnlyList<string> GetFiles(string directory)
        {
            var dirId = FolderId(directory);
            var results = new List<string>();
            foreach (var file in ListChildren(dirId, foldersOnly: false))
            {
                var full = Combine(directory, file.Name);
                _files[Normalize(full)] = ToEntry(file);
                results.Add(full);
            }
            return results;
        }

        public IReadOnlyList<string> GetDirectories(string directory)
        {
            var dirId = FolderId(directory);
            var results = new List<string>();
            foreach (var dir in ListChildren(dirId, foldersOnly: true))
            {
                var full = Combine(directory, dir.Name);
                _folderIds[Normalize(full)] = dir.Id;
                results.Add(full);
            }
            return results;
        }

        public DateTime GetLastWriteTimeUtc(string path) => GetFileEntry(path).WriteTimeUtc;

        public long GetFileSize(string path) => GetFileEntry(path).Size;

        public void SetLastWriteTimeUtc(string path, DateTime value)
        {
            var entry = GetFileEntry(path);
            var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);

            var metadata = new DriveFile
            {
                ModifiedTimeRaw = Rfc3339(utc),
                // Keep the exact source ticks so a round-trip compares equal despite Drive's ms precision.
                AppProperties = new Dictionary<string, string> { [WriteTicksKey] = utc.Ticks.ToString(CultureInfo.InvariantCulture) },
            };
            var update = _drive.Files.Update(metadata, entry.Id);
            update.Fields = EntryFields;
            var result = update.Execute();
            _files[Normalize(path)] = ToEntry(result);
        }

        public Stream OpenRead(string path)
        {
            var entry = GetFileEntry(path);
            var tempPath = LocalTempPath();
            using (var fileStream = System.IO.File.Create(tempPath))
            {
                _drive.Files.Get(entry.Id).Download(fileStream);
            }
            // The OS removes the temp when the returned stream is disposed.
            return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }

        public Stream OpenWrite(string path)
        {
            var normalized = Normalize(path);
            var (parentPath, name) = SplitParent(normalized);
            var parentId = FolderId(parentPath);
            var existingId = FindChild(parentId, name, isFolder: false)?.Id;
            return new UploadStream(this, normalized, parentId, name, existingId);
        }

        public void CopyFile(string source, string destination, bool overwrite)
        {
            using var input = OpenRead(source);
            using var output = OpenWrite(destination);
            input.CopyTo(output);
        }

        public void MoveFile(string source, string destination, bool overwrite)
        {
            var sourceNorm = Normalize(source);
            var destNorm = Normalize(destination);

            var sourceEntry = GetFileEntry(source);
            var (sourceParent, _) = SplitParent(sourceNorm);
            var (destParent, destName) = SplitParent(destNorm);
            var sourceParentId = FolderId(sourceParent);
            var destParentId = FolderId(destParent);

            if (overwrite)
            {
                var existing = FindChild(destParentId, destName, isFolder: false);
                if (existing is not null)
                {
                    _drive.Files.Delete(existing.Id).Execute();
                    _files.Remove(destNorm);
                }
            }

            var update = _drive.Files.Update(new DriveFile { Name = destName }, sourceEntry.Id);
            update.Fields = EntryFields;
            if (!string.Equals(destParentId, sourceParentId, StringComparison.Ordinal))
            {
                update.AddParents = destParentId;
                update.RemoveParents = sourceParentId;
            }
            var result = update.Execute();

            _files.Remove(sourceNorm);
            _files[destNorm] = ToEntry(result);
        }

        public void DeleteFile(string path)
        {
            var normalized = Normalize(path);
            string id;
            if (_files.TryGetValue(normalized, out var entry))
            {
                id = entry.Id;
            }
            else
            {
                var (parentPath, name) = SplitParent(normalized);
                if (!TryGetFolderId(parentPath, out var parentId))
                {
                    return;
                }
                var file = FindChild(parentId, name, isFolder: false);
                if (file is null)
                {
                    return;
                }
                id = file.Id;
            }

            _drive.Files.Delete(id).Execute();
            _files.Remove(normalized);
        }

        public bool FilesContentEqual(string a, string b)
        {
            using var streamA = OpenRead(a);
            using var streamB = OpenRead(b);
            return StreamCompare.Equal(streamA, streamB);
        }

        public string GetTempFilePath(string fileName) =>
            throw new NotSupportedException("Temp files are local-only; archives are built locally then copied to the remote.");

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true) =>
            throw new NotSupportedException("Zipping from a remote source is not supported.");

        public string? GetZipComment(string path)
        {
            // Read just the tail of the archive (where the EOCD record + comment live) via a ranged download,
            // rather than fetching the whole file. Mirrors the SMB implementation.
            try
            {
                var entry = GetFileEntry(path);
                if (entry.Size < 22)
                {
                    return null;
                }

                const int maxComment = 65535;
                var window = (int)Math.Min(entry.Size, 22 + maxComment);
                var tail = ReadTail(entry.Id, entry.Size - window, window);
                return ParseEocdComment(tail);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException or HttpRequestException)
            {
                return null; // unreadable — treat as "no fingerprint" so the caller rebuilds
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _drive.Dispose();
        }

        // ---- internals ----

        // Uploads the buffered temp to Drive (create or update) and caches the resulting entry.
        private void CompleteUpload(string normalizedPath, string parentId, string name, string? existingId, Stream content)
        {
            DriveFile result;
            if (existingId is not null)
            {
                var request = _drive.Files.Update(new DriveFile(), existingId, content, OctetStream);
                request.Fields = EntryFields;
                var progress = request.Upload();
                if (progress.Status == UploadStatus.Failed)
                {
                    throw progress.Exception ?? new IOException($"Upload of '{name}' failed.");
                }
                result = request.ResponseBody;
            }
            else
            {
                var metadata = new DriveFile { Name = name, Parents = [parentId] };
                var request = _drive.Files.Create(metadata, content, OctetStream);
                request.Fields = EntryFields;
                var progress = request.Upload();
                if (progress.Status == UploadStatus.Failed)
                {
                    throw progress.Exception ?? new IOException($"Upload of '{name}' failed.");
                }
                result = request.ResponseBody;
            }

            _files[normalizedPath] = ToEntry(result);
        }

        private DriveEntry GetFileEntry(string path)
        {
            var normalized = Normalize(path);
            if (_files.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            var (parentPath, name) = SplitParent(normalized);
            var parentId = FolderId(parentPath);
            var file = FindChild(parentId, name, isFolder: false)
                ?? throw new FileNotFoundException($"Drive file '{path}' was not found.", path);

            var entry = ToEntry(file);
            _files[normalized] = entry;
            return entry;
        }

        private string FolderId(string path) =>
            TryGetFolderId(path, out var id) ? id : throw new DirectoryNotFoundException($"Drive folder '{path}' was not found.");

        private bool TryGetFolderId(string path, out string id)
        {
            var normalized = Normalize(path);
            if (_folderIds.TryGetValue(normalized, out id!))
            {
                return true;
            }
            if (normalized.Length == 0)
            {
                id = "root";
                _folderIds[string.Empty] = id;
                return true;
            }

            var (parentPath, name) = SplitParent(normalized);
            if (!TryGetFolderId(parentPath, out var parentId))
            {
                id = string.Empty;
                return false;
            }

            var child = FindChild(parentId, name, isFolder: true);
            if (child is null)
            {
                id = string.Empty;
                return false;
            }

            id = child.Id;
            _folderIds[normalized] = id;
            return true;
        }

        private DriveFile? FindChild(string parentId, string name, bool isFolder)
        {
            var typeClause = isFolder ? $"mimeType = '{FolderMimeType}'" : $"mimeType != '{FolderMimeType}'";
            var list = _drive.Files.List();
            list.Q = $"name = '{Escape(name)}' and '{Escape(parentId)}' in parents and {typeClause} and trashed = false";
            list.Fields = $"files({EntryFields})";
            list.PageSize = 10;
            var files = list.Execute().Files;
            return files is { Count: > 0 } ? files[0] : null;
        }

        private List<DriveFile> ListChildren(string parentId, bool foldersOnly)
        {
            var typeClause = foldersOnly ? $"mimeType = '{FolderMimeType}'" : $"mimeType != '{FolderMimeType}'";
            var items = new List<DriveFile>();
            string? pageToken = null;
            do
            {
                var list = _drive.Files.List();
                list.Q = $"'{Escape(parentId)}' in parents and {typeClause} and trashed = false";
                list.Fields = $"nextPageToken, files({EntryFields})";
                list.PageSize = 1000;
                list.PageToken = pageToken;
                var response = list.Execute();
                if (response.Files is { Count: > 0 })
                {
                    items.AddRange(response.Files);
                }
                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
            return items;
        }

        // Ranged download of [offset, offset+count) via the authenticated HTTP client.
        private byte[] ReadTail(string fileId, long offset, int count)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
            request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
            using var response = _drive.HttpClient.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        private static DriveEntry ToEntry(DriveFile file)
        {
            var isFolder = string.Equals(file.MimeType, FolderMimeType, StringComparison.Ordinal);
            return new DriveEntry(file.Id, file.Name, isFolder, file.Size ?? 0, ParseWriteTime(file));
        }

        private static DateTime ParseWriteTime(DriveFile file)
        {
            if (file.AppProperties is not null
                && file.AppProperties.TryGetValue(WriteTicksKey, out var ticksText)
                && long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }

            if (!string.IsNullOrEmpty(file.ModifiedTimeRaw)
                && DateTimeOffset.TryParse(file.ModifiedTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified))
            {
                return modified.UtcDateTime;
            }

            return DateTime.MinValue;
        }

        private static string? ParseEocdComment(byte[] tail)
        {
            // EOCD: signature(4 = PK\x05\x06) ... commentLength(2 @ +20) comment(@ +22). Scan backwards.
            for (var i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    var commentLength = tail[i + 20] | (tail[i + 21] << 8);
                    var start = i + 22;
                    if (commentLength == 0 || start + commentLength > tail.Length)
                    {
                        return null;
                    }
                    return System.Text.Encoding.UTF8.GetString(tail, start, commentLength);
                }
            }
            return null;
        }

        private void InvalidateUnder(string normalizedPath)
        {
            var prefix = normalizedPath + "\\";
            foreach (var key in _folderIds.Keys.Where(k => k.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _folderIds.Remove(key);
            }
            foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _files.Remove(key);
            }
        }

        private static string LocalTempPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "BackupService", "gdrive");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static string Combine(string directory, string name) =>
            string.IsNullOrEmpty(directory) ? name : $@"{directory.TrimEnd('\\')}\{name}";

        private static (string ParentPath, string Name) SplitParent(string normalized)
        {
            var index = normalized.LastIndexOf('\\');
            return index < 0 ? (string.Empty, normalized) : (normalized[..index], normalized[(index + 1)..]);
        }

        private static string Rfc3339(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

        private static string Normalize(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').Trim('\\');

        private sealed record DriveEntry(string Id, string Name, bool IsFolder, long Size, DateTime WriteTimeUtc);

        // A write stream that buffers to a local temp file and uploads it to Drive on close.
        private sealed class UploadStream : Stream
        {
            private readonly GoogleDriveBackupFileSystem _fs;
            private readonly string _normalizedPath;
            private readonly string _parentId;
            private readonly string _name;
            private readonly string? _existingId;
            private readonly string _tempPath;
            private readonly FileStream _temp;
            private bool _completed;

            public UploadStream(GoogleDriveBackupFileSystem fs, string normalizedPath, string parentId, string name, string? existingId)
            {
                _fs = fs;
                _normalizedPath = normalizedPath;
                _parentId = parentId;
                _name = name;
                _existingId = existingId;
                _tempPath = LocalTempPath();
                _temp = System.IO.File.Create(_tempPath);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _temp.Length;
            public override long Position { get => _temp.Position; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => _temp.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => _temp.Write(buffer);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _temp.WriteAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _temp.WriteAsync(buffer, cancellationToken);
            public override void Flush() => _temp.Flush();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_completed)
                {
                    _completed = true;
                    try
                    {
                        _temp.Flush();
                        _temp.Position = 0;
                        _fs.CompleteUpload(_normalizedPath, _parentId, _name, _existingId, _temp);
                    }
                    finally
                    {
                        _temp.Dispose();
                        try { System.IO.File.Delete(_tempPath); } catch { /* best-effort */ }
                    }
                }
                base.Dispose(disposing);
            }
        }
    }
}
