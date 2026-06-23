using System.Net;
using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;

namespace BackupService.Connections.Smb
{
    /// <summary>
    /// Default <see cref="ISmbConnector"/> over SMBLibrary's <see cref="SMB2Client"/>. The SMB calls are
    /// blocking, so each public method runs on a background thread. A fresh client is connected and torn
    /// down per call.
    /// </summary>
    public sealed class SmbConnector(ILogger<SmbConnector> logger) : ISmbConnector
    {
        public Task<ConnectionTestResult> TestAsync(SmbConnectionInfo info, CancellationToken cancellationToken = default) =>
            Task.Run(() =>
            {
                try
                {
                    return RunWithStore(info, (store, rootPath) =>
                    {
                        // Opening (and listing) the root folder proves auth + share + path are all good.
                        var listStatus = TryListDirectories(store, rootPath, out _);
                        return listStatus == NTStatus.STATUS_SUCCESS
                            ? ConnectionTestResult.Success("Connected successfully.")
                            : ConnectionTestResult.Failure($"Connected, but the root folder could not be opened ({Describe(listStatus)}).");
                    });
                }
                catch (SmbBrowseException ex)
                {
                    return ConnectionTestResult.Failure(ex.Message);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SMB test connection failed for {Host}", info.Host);
                    return ConnectionTestResult.Failure($"Connection failed: {ex.Message}");
                }
            }, cancellationToken);

        public Task<IReadOnlyList<string>> ListDirectoriesAsync(SmbConnectionInfo info, string relativePath, CancellationToken cancellationToken = default) =>
            Task.Run<IReadOnlyList<string>>(() =>
                RunWithStore(info, (store, _) =>
                {
                    var path = NormalizePath(relativePath);
                    var status = TryListDirectories(store, path, out var names);
                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        throw new SmbBrowseException($"Could not list '{relativePath}' ({Describe(status)}).");
                    }
                    return (IReadOnlyList<string>)names;
                }), cancellationToken);

        public Task CreateDirectoryAsync(SmbConnectionInfo info, string relativePath, CancellationToken cancellationToken = default) =>
            Task.Run(() => RunWithStore(info, (store, _root) =>
            {
                var path = NormalizePath(relativePath);
                if (path.Length == 0)
                {
                    throw new SmbBrowseException("A folder name is required.");
                }

                var status = store.CreateFile(
                    out var handle, out _, path,
                    AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                    CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, securityContext: null);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new SmbBrowseException($"Could not create folder '{relativePath}' ({Describe(status)}).");
                }
                store.CloseFile(handle);
                return true;
            }), cancellationToken);

        public Task<StorageSpace?> GetFreeSpaceAsync(SmbConnectionInfo info, CancellationToken cancellationToken = default) =>
            Task.Run<StorageSpace?>(() =>
            {
                try
                {
                    return RunWithStore<StorageSpace?>(info, (store, _root) =>
                    {
                        // Query the tree-connected share's volume for caller-available vs total capacity.
                        var status = store.GetFileSystemInformation(out var result, FileSystemInformationClass.FileFsFullSizeInformation);
                        if (status != NTStatus.STATUS_SUCCESS || result is not FileFsFullSizeInformation fs)
                        {
                            return null;
                        }

                        var bytesPerUnit = (long)fs.SectorsPerAllocationUnit * fs.BytesPerSector;
                        var free = fs.CallerAvailableAllocationUnits * bytesPerUnit;
                        var total = fs.TotalAllocationUnits * bytesPerUnit;
                        return new StorageSpace(total, free, Unlimited: false);
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read SMB free space for {Host}", info.Host);
                    return null;
                }
            }, cancellationToken);

        /// <summary>Connects, logs in, tree-connects the share, runs <paramref name="action"/>, then tears everything down.</summary>
        private T RunWithStore<T>(SmbConnectionInfo info, Func<ISMBFileStore, string, T> action)
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
                    throw new SmbBrowseException($"Login failed ({Describe(loginStatus)}).");
                }

                var store = client.TreeConnect(info.Share, out var treeStatus);
                if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
                {
                    throw new SmbBrowseException($"Could not open share '{info.Share}' ({Describe(treeStatus)}).");
                }

                try
                {
                    return action(store, NormalizePath(info.RootFolder));
                }
                finally
                {
                    store.Disconnect();
                }
            }
            finally
            {
                try
                {
                    client.Logoff();
                }
                catch
                {
                    // best-effort
                }
                client.Disconnect();
            }
        }

        /// <summary>Opens the directory at <paramref name="path"/> and returns its immediate sub-folder names.</summary>
        private static NTStatus TryListDirectories(ISMBFileStore store, string path, out List<string> names)
        {
            names = [];

            var status = store.CreateFile(
                out var handle,
                out _,
                path,
                AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                securityContext: null);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                return status;
            }

            try
            {
                var queryStatus = store.QueryDirectory(out var entries, handle, "*", FileInformationClass.FileDirectoryInformation);
                if (queryStatus != NTStatus.STATUS_SUCCESS && queryStatus != NTStatus.STATUS_NO_MORE_FILES)
                {
                    return queryStatus;
                }

                foreach (var entry in entries)
                {
                    if (entry is FileDirectoryInformation info
                        && (info.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0
                        && info.FileName is not ("." or ".."))
                    {
                        names.Add(info.FileName);
                    }
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);
                return NTStatus.STATUS_SUCCESS;
            }
            finally
            {
                store.CloseFile(handle);
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

        /// <summary>SMB paths are share-relative, backslash-separated, with no leading/trailing separator.</summary>
        private static string NormalizePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            return relativePath.Replace('/', '\\').Trim('\\');
        }

        private static string Describe(NTStatus status) => status switch
        {
            NTStatus.STATUS_LOGON_FAILURE => "bad username or password",
            NTStatus.STATUS_ACCESS_DENIED => "access denied",
            NTStatus.STATUS_BAD_NETWORK_NAME => "share not found",
            NTStatus.STATUS_OBJECT_NAME_NOT_FOUND => "path not found",
            NTStatus.STATUS_OBJECT_PATH_NOT_FOUND => "path not found",
            _ => status.ToString(),
        };
    }
}
