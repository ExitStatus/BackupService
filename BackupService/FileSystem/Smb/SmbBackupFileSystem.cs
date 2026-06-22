using System.Net;
using System.Net.Sockets;
using BackupService.Connections;
using BackupService.Connections.Smb;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace BackupService.FileSystem.Smb
{
    /// <summary>
    /// <see cref="IBackupFileSystem"/> over a live SMBLibrary session. Paths are share-relative
    /// (backslash-separated, no leading separator). Holds an open <see cref="SMB2Client"/> + file store
    /// for the session's lifetime; <see cref="Dispose"/> tears the connection down.
    /// <para>
    /// Created by <see cref="EndpointFileSystemFactory"/> per run. The archive-only members
    /// (<see cref="GetTempFilePath"/>/<see cref="CreateZipFromDirectory"/>) are not supported remotely —
    /// ArchiveSync builds its zip locally and only copies the finished file to the target.
    /// </para>
    /// </summary>
    public sealed class SmbBackupFileSystem : IBackupFileSystem, IDisposable
    {
        private readonly SMB2Client _client;
        private readonly ISMBFileStore _store;
        private bool _disposed;

        private SmbBackupFileSystem(SMB2Client client, ISMBFileStore store)
        {
            _client = client;
            _store = store;
        }

        /// <summary>Connects, authenticates and tree-connects the share, returning a ready filesystem.</summary>
        public static SmbBackupFileSystem Connect(SmbConnectionInfo info)
        {
            var address = Resolve(info.Host);
            var client = new SMB2Client();
            try
            {
                if (!client.Connect(address, SMBTransportType.DirectTCPTransport))
                {
                    throw new SmbBrowseException($"Could not connect to {info.Host}.");
                }

                var loginStatus = client.Login(info.Domain ?? string.Empty, info.Username, info.Password);
                if (loginStatus != NTStatus.STATUS_SUCCESS)
                {
                    throw new SmbBrowseException($"SMB login failed for {info.Host} ({loginStatus}).");
                }

                var store = client.TreeConnect(info.Share, out var treeStatus);
                if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
                {
                    throw new SmbBrowseException($"Could not open share '{info.Share}' ({treeStatus}).");
                }

                return new SmbBackupFileSystem(client, store);
            }
            catch
            {
                try { client.Logoff(); } catch { /* best-effort */ }
                client.Disconnect();
                throw;
            }
        }

        public bool DirectoryExists(string path) => Exists(path, directory: true);

        public bool FileExists(string path) => Exists(path, directory: false);

        public void CreateDirectory(string path)
        {
            // Make each missing segment in turn (mkdir -p).
            var segments = Normalize(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var segment in segments)
            {
                current = current.Length == 0 ? segment : $@"{current}\{segment}";
                var status = _store.CreateFile(
                    out var handle, out _, current,
                    AccessMask.GENERIC_READ, FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                    CreateDisposition.FILE_OPEN_IF, CreateOptions.FILE_DIRECTORY_FILE, null);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Could not create remote folder '{current}' ({status}).");
                }
                _store.CloseFile(handle);
            }
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            if (recursive)
            {
                foreach (var file in GetFiles(path))
                {
                    DeleteFile(file);
                }
                foreach (var dir in GetDirectories(path))
                {
                    DeleteDirectory(dir, recursive: true);
                }
            }

            DeleteEntry(path, directory: true);
        }

        public IReadOnlyList<string> GetFiles(string directory) => List(directory, wantDirectories: false);

        public IReadOnlyList<string> GetDirectories(string directory) => List(directory, wantDirectories: true);

        public DateTime GetLastWriteTimeUtc(string path)
        {
            var basic = (FileBasicInformation)GetInfo(path, FileInformationClass.FileBasicInformation);
            var lastWrite = (DateTime?)basic.LastWriteTime ?? DateTime.MinValue;
            return DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc);
        }

        public long GetFileSize(string path)
        {
            var standard = (FileStandardInformation)GetInfo(path, FileInformationClass.FileStandardInformation);
            return standard.EndOfFile;
        }

        public void SetLastWriteTimeUtc(string path, DateTime value)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{path}' to set timestamp");
            try
            {
                var info = new FileBasicInformation { LastWriteTime = DateTime.SpecifyKind(value, DateTimeKind.Utc) };
                Ensure(_store.SetFileInformation(handle, info), $"set timestamp of '{path}'");
            }
            finally
            {
                _store.CloseFile(handle);
            }
        }

        public Stream OpenRead(string path)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{path}' for reading");
            return new SmbReadStream(_store, handle, (int)_client.MaxReadSize);
        }

        public Stream OpenWrite(string path)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{path}' for writing");
            return new SmbWriteStream(_store, handle, (int)_client.MaxWriteSize);
        }

        public void CopyFile(string source, string destination, bool overwrite)
        {
            using var src = OpenRead(source);
            using var dst = OpenWrite(destination);
            src.CopyTo(dst);
        }

        public void MoveFile(string source, string destination, bool overwrite)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(source),
                AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{source}' to move");
            try
            {
                var rename = new FileRenameInformationType2
                {
                    ReplaceIfExists = overwrite,
                    FileName = Normalize(destination),
                };
                Ensure(_store.SetFileInformation(handle, rename), $"move '{source}' -> '{destination}'");
            }
            finally
            {
                _store.CloseFile(handle);
            }
        }

        public void DeleteFile(string path) => DeleteEntry(path, directory: false);

        public bool FilesContentEqual(string a, string b)
        {
            using var streamA = OpenRead(a);
            using var streamB = OpenRead(b);
            return StreamCompare.Equal(streamA, streamB);
        }

        public string GetTempFilePath(string fileName) =>
            throw new NotSupportedException("Temp files are local-only; archives are built locally then copied to the remote.");

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null) =>
            throw new NotSupportedException("Zipping from a remote source is not supported.");

        public string? GetZipComment(string path)
        {
            // The ZIP archive comment lives in the End-Of-Central-Directory (EOCD) record at the very end of
            // the file, so read just the tail and parse it (rather than downloading the whole archive). The
            // EOCD is 22 bytes + an up-to-65535-byte comment, so the last (22 + 65535) bytes always cover it.
            try
            {
                var size = GetFileSize(path);
                if (size < 22)
                {
                    return null;
                }

                const int maxComment = 65535;
                var window = (int)Math.Min(size, 22 + maxComment);
                var tail = ReadTail(path, size - window, window);
                return ParseEocdComment(tail);
            }
            catch (Exception ex) when (ex is IOException or SmbBrowseException)
            {
                return null; // unreadable — treat as "no fingerprint" so the caller rebuilds
            }
        }

        // Reads <paramref name="count"/> bytes starting at <paramref name="offset"/> (chunked by MaxReadSize).
        private byte[] ReadTail(string path, long offset, int count)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{path}' to read tail");
            try
            {
                var buffer = new byte[count];
                var written = 0;
                var max = (int)_client.MaxReadSize;
                while (written < count)
                {
                    var chunk = Math.Min(max, count - written);
                    var readStatus = _store.ReadFile(out var data, handle, offset + written, chunk);
                    if (readStatus == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
                    {
                        break;
                    }
                    Ensure(readStatus, $"read '{path}'");
                    Array.Copy(data, 0, buffer, written, data.Length);
                    written += data.Length;
                }
                return written == count ? buffer : buffer[..written];
            }
            finally
            {
                _store.CloseFile(handle);
            }
        }

        // Finds the EOCD signature (PK\x05\x06) from the end of <paramref name="tail"/> and returns its
        // UTF-8 comment, or null if the signature/comment can't be read (e.g. ZIP64 or an unexpected layout).
        private static string? ParseEocdComment(byte[] tail)
        {
            // EOCD: signature(4) ... commentLength(2 @ +20) comment(@ +22). Scan backwards for the signature.
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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try { _store.Disconnect(); } catch { /* best-effort */ }
            try { _client.Logoff(); } catch { /* best-effort */ }
            _client.Disconnect();
        }

        // ---- helpers ----

        private bool Exists(string path, bool directory)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0)
            {
                return directory; // the share root is a directory
            }

            var status = _store.CreateFile(
                out var handle, out _, normalized,
                AccessMask.GENERIC_READ, directory ? FileAttributes.Directory : FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE,
                null);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                return false;
            }
            _store.CloseFile(handle);
            return true;
        }

        private IReadOnlyList<string> List(string directory, bool wantDirectories)
        {
            var dir = Normalize(directory);
            var status = _store.CreateFile(
                out var handle, out _, dir,
                AccessMask.GENERIC_READ, FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            Ensure(status, $"open folder '{directory}'");

            try
            {
                var queryStatus = _store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (queryStatus != NTStatus.STATUS_SUCCESS && queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                {
                    throw new IOException($"Could not list '{directory}' ({queryStatus}).");
                }

                var results = new List<string>();
                foreach (var entry in entries)
                {
                    if (entry is not FileDirectoryInformation info || info.FileName is "." or "..")
                    {
                        continue;
                    }
                    var isDirectory = (info.FileAttributes & FileAttributes.Directory) != 0;
                    if (isDirectory == wantDirectories)
                    {
                        results.Add(dir.Length == 0 ? info.FileName : $@"{dir}\{info.FileName}");
                    }
                }
                return results;
            }
            finally
            {
                _store.CloseFile(handle);
            }
        }

        private FileInformation GetInfo(string path, FileInformationClass infoClass)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            Ensure(status, $"open '{path}'");
            try
            {
                Ensure(_store.GetFileInformation(out var info, handle, infoClass), $"read info of '{path}'");
                return info;
            }
            finally
            {
                _store.CloseFile(handle);
            }
        }

        private void DeleteEntry(string path, bool directory)
        {
            var status = _store.CreateFile(
                out var handle, out _, Normalize(path),
                AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                (directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_DELETE_ON_CLOSE,
                null);
            Ensure(status, $"open '{path}' to delete");

            // FILE_DELETE_ON_CLOSE deletes the entry when the handle closes.
            var disposition = new FileDispositionInformation { DeletePending = true };
            _store.SetFileInformation(handle, disposition);
            _store.CloseFile(handle);
        }

        private static void Ensure(NTStatus status, string action)
        {
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB failed to {action} ({status}).");
            }
        }

        private static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                return ip;
            }

            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault()
                ?? throw new SmbBrowseException($"Could not resolve host '{host}'.");
        }

        private static string Normalize(string? path) =>
            string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\').Trim('\\');
    }
}
