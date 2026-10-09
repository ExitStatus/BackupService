using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using BackupService.Connections.GoogleDrive;
using Google.Apis.Download;
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
    /// <para>
    /// Google Workspace files (Docs, Sheets, Slides, shortcuts) are never produced by a backup, so one on Drive is
    /// always the user's own work: reading one throws <see cref="FileNotDownloadableException"/> (it has no content
    /// to download), and deleting or overwriting one — or deleting a folder that holds one — throws
    /// <see cref="ProtectedFileException"/>. The engines log both as warnings and leave the file alone.
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

            // Deleting a Drive folder removes its whole subtree in one go, so never take a Google Workspace file down
            // with it. (The engines empty a folder before a non-recursive delete and leave it alone when something
            // inside was kept; this is the safety net, and it holds for a recursive delete too.)
            if (ContainsWorkspaceFile(id))
            {
                throw new ProtectedFileException(
                    $"Drive folder '{path}' contains Google Docs, Sheets or Slides files.",
                    "it contains Google Docs, Sheets or Slides files, which backups never delete on Google Drive");
            }

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
            foreach (var file in MappableChildren(directory, ListChildren(dirId, foldersOnly: false)))
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
            foreach (var dir in MappableChildren(directory, ListChildren(dirId, foldersOnly: true)))
            {
                var full = Combine(directory, dir.Name);
                _folderIds[Normalize(full)] = dir.Id;
                results.Add(full);
            }
            return results;
        }

        public DateTime GetLastWriteTimeUtc(string path) => GetFileEntry(path).WriteTimeUtc;

        public long GetFileSize(string path) => GetFileEntry(path).Size;

        public FileStat GetFileStat(string path)
        {
            var entry = GetFileEntry(path);
            return new FileStat(entry.WriteTimeUtc, entry.Size);
        }

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

        public Stream OpenRead(string path) => OpenRead(path, CancellationToken.None);

        public Stream OpenRead(string path, CancellationToken cancellationToken)
        {
            var entry = GetFileEntry(path);
            // Google's own formats have no file content to download (Drive can only export them), so skip them up
            // front — the engines log this as a warning — rather than make a request that's bound to fail.
            if (NonDownloadableKind(entry.MimeType) is { } kind)
            {
                throw new FileNotDownloadableException(
                    $"Drive file '{path}' is a {kind} file, which Google Drive can export but not download.",
                    $"it's a {kind} file, which Google Drive can export but not download, so it isn't backed up");
            }

            var tempPath = LocalTempPath();
            try
            {
                try
                {
                    using var fileStream = System.IO.File.Create(tempPath);
                    var progress = _drive.Files.Get(entry.Id).DownloadAsync(fileStream, cancellationToken).GetAwaiter().GetResult();
                    cancellationToken.ThrowIfCancellationRequested();
                    fileStream.Flush();
                    EnsureCompleteDownload(progress, fileStream.Length, entry.Size, () => RefreshFileEntry(path).Size, path);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // An HTTP timeout surfaces as a TaskCanceledException. That's a failed download, not the user
                    // pressing Stop — passed on as-is, the engines would abandon the whole run as "cancelled".
                    throw new IOException($"Download of Drive file '{path}' timed out.", ex);
                }
            }
            catch
            {
                // Never leave a partial download behind.
                try { System.IO.File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
            // The OS removes the temp when the returned stream is disposed.
            return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }

        /// <summary>
        /// For a Google Workspace type (<c>application/vnd.google-apps.*</c> — Docs, Sheets, Slides, a shortcut, …),
        /// which Drive stores without downloadable content, the name to show for it; null for an ordinary file.
        /// </summary>
        internal static string? NonDownloadableKind(string? mimeType)
        {
            const string WorkspacePrefix = "application/vnd.google-apps.";
            if (mimeType is null || !mimeType.StartsWith(WorkspacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return mimeType[WorkspacePrefix.Length..].ToLowerInvariant() switch
            {
                "document" => "Google Docs",
                "spreadsheet" => "Google Sheets",
                "presentation" => "Google Slides",
                "drawing" => "Google Drawings",
                "form" => "Google Forms",
                "site" => "Google Sites",
                "script" => "Apps Script",
                "shortcut" => "Drive shortcut",
                _ => "Google Workspace",
            };
        }

        /// <summary>
        /// The warning reason for refusing to delete or overwrite a file of <paramref name="mimeType"/> — a Google
        /// Workspace file, which this app never creates, so one on Drive is always the user's own — or null when the
        /// file is ordinary and may be replaced.
        /// </summary>
        internal static string? ProtectedReason(string? mimeType) =>
            NonDownloadableKind(mimeType) is { } kind
                ? $"it's a {kind} file, which backups never delete or overwrite on Google Drive"
                : null;

        private static void ThrowIfProtected(string? mimeType, string path)
        {
            if (ProtectedReason(mimeType) is { } reason)
            {
                throw new ProtectedFileException($"Drive file '{path}' is protected: {reason}.", reason);
            }
        }

        /// <summary>
        /// Throws unless a download completed in full. Drive's media downloader reports a failure through the returned
        /// progress rather than throwing, so a download that broke off part-way used to hand back a silently truncated
        /// file. A completed download shorter than the size Drive reported is also refused — but the cached size may just
        /// be stale (the file shrank after it was listed), so it is re-read once (<paramref name="refreshSize"/>) before
        /// the download is declared incomplete. A size of zero (Drive reports none for some items) gives nothing to check against.
        /// </summary>
        internal static void EnsureCompleteDownload(IDownloadProgress progress, long downloaded, long expectedSize, Func<long> refreshSize, string path)
        {
            if (progress.Status != DownloadStatus.Completed)
            {
                if (progress.Exception is { } failure)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack trace
                }
                throw new IOException($"Download of Drive file '{path}' did not complete ({progress.Status}).");
            }

            if (expectedSize > 0 && downloaded < expectedSize)
            {
                var currentSize = refreshSize();
                if (downloaded < currentSize)
                {
                    throw new IOException($"Incomplete download of Drive file '{path}': received {downloaded} of {currentSize} bytes.");
                }
            }
        }

        public Stream OpenWrite(string path)
        {
            var normalized = Normalize(path);
            var (parentPath, name) = SplitParent(normalized);
            var parentId = FolderId(parentPath);
            var existing = FindChild(parentId, name, isFolder: false);
            ThrowIfProtected(existing?.MimeType, path); // never replace a Google Docs file's content
            return new UploadStream(this, normalized, parentId, name, existing?.Id);
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
            ThrowIfProtected(sourceEntry.MimeType, source); // don't rename a Google Docs file either (e.g. as a conflict copy)
            var (sourceParent, _) = SplitParent(sourceNorm);
            var (destParent, destName) = SplitParent(destNorm);
            var sourceParentId = FolderId(sourceParent);
            var destParentId = FolderId(destParent);

            if (overwrite)
            {
                var existing = FindChild(destParentId, destName, isFolder: false);
                if (existing is not null)
                {
                    ThrowIfProtected(existing.MimeType, destination);
                    _drive.Files.Delete(existing.Id).Execute();
                    _files.Remove(destNorm);
                }
            }

            // A metadata-only files.update (rename/move) otherwise resets Drive's modifiedTime to "now", which
            // would lose the source timestamp the crash-safe copy just stamped on the temp via
            // SetLastWriteTimeUtc. Re-assert it in the same request (appProperties — incl. the exact ticks —
            // are preserved since they aren't part of this update).
            var metadata = new DriveFile { Name = destName };
            if (sourceEntry.WriteTimeUtc > DateTime.MinValue)
            {
                metadata.ModifiedTimeRaw = Rfc3339(sourceEntry.WriteTimeUtc);
            }

            var update = _drive.Files.Update(metadata, sourceEntry.Id);
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
            if (!_files.TryGetValue(normalized, out var entry))
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
                entry = ToEntry(file);
            }

            ThrowIfProtected(entry.MimeType, path);
            _drive.Files.Delete(entry.Id).Execute();
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

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, System.IO.Compression.CompressionLevel compressionLevel = System.IO.Compression.CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null) =>
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

        // Drops the cached entry and re-reads it from Drive, so later size/time reads in this session see the update.
        private DriveEntry RefreshFileEntry(string path)
        {
            _files.Remove(Normalize(path));
            return GetFileEntry(path);
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
            if (files is { Count: > 1 })
            {
                // Drive allows same-named siblings; picking one arbitrarily would act on the wrong item.
                throw DuplicateName(parentId, name);
            }
            return files is { Count: 1 } ? files[0] : null;
        }

        // The children a name-path can address. Drive allows same-named siblings (and names like ".."), but the engine
        // works in paths: two items named alike map to ONE path, so one would be backed up twice and the other never
        // — and with deletions on, its earlier copies would be deleted as orphans. So a duplicate fails the listing
        // (the engine logs the folder as an error and skips it, deleting nothing there), and "." / ".." — which would
        // escape the target folder when joined onto a local path — are left out.
        internal static List<DriveFile> MappableChildren(string directory, List<DriveFile> children)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // the engines compare names ignoring case
            var mappable = new List<DriveFile>(children.Count);
            foreach (var child in children)
            {
                if (child.Name is null or "" or "." or "..")
                {
                    continue;
                }
                if (!seen.Add(child.Name))
                {
                    throw DuplicateName(directory, child.Name);
                }
                mappable.Add(child);
            }
            return mappable;
        }

        private static IOException DuplicateName(string folder, string name) =>
            new($"Drive folder '{folder}' holds more than one item named '{name}' (names must be unique, ignoring case, to be backed up) — rename one of them.");

        // True if a Google Workspace file sits anywhere in the folder's subtree.
        private bool ContainsWorkspaceFile(string folderId) =>
            ListChildren(folderId, foldersOnly: false).Any(f => NonDownloadableKind(f.MimeType) is not null)
            || ListChildren(folderId, foldersOnly: true).Any(f => ContainsWorkspaceFile(f.Id));

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
            return new DriveEntry(file.Id, file.Name, isFolder, file.Size ?? 0, ParseWriteTime(file), file.MimeType);
        }

        internal static DateTime ParseWriteTime(DriveFile file)
        {
            long? stamped = file.AppProperties is not null
                && file.AppProperties.TryGetValue(WriteTicksKey, out var ticksText)
                && long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    ? ticks
                    : null;
            DateTime? modified = !string.IsNullOrEmpty(file.ModifiedTimeRaw)
                && DateTimeOffset.TryParse(file.ModifiedTimeRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed.UtcDateTime
                    : null;

            // The exact ticks this app stamped are only the file's time while Drive's own (millisecond) modifiedTime
            // still agrees with them. appProperties survive later revisions, so after someone edits the file in Drive
            // (or uploads a new version) modifiedTime moves on while the old stamp stays — trusting the stamp then
            // would hide the edit, and a two-way sync could overwrite it.
            if (stamped is { } exact && (modified is null || Math.Abs(modified.Value.Ticks - exact) <= TimeSpan.TicksPerMillisecond))
            {
                return new DateTime(exact, DateTimeKind.Utc);
            }

            return modified ?? DateTime.MinValue;
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

        private sealed record DriveEntry(string Id, string Name, bool IsFolder, long Size, DateTime WriteTimeUtc, string? MimeType);

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
