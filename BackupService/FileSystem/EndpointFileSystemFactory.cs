using BackupService.Connections;
using BackupService.Enumerations;
using BackupService.FileSystem.GoogleDrive;
using BackupService.FileSystem.Smb;

namespace BackupService.FileSystem
{
    /// <summary>
    /// Default <see cref="IEndpointFileSystemFactory"/>: a null connection id resolves to the shared local
    /// filesystem; a set id resolves the connection by type (SMB or Google Drive, decrypting its secrets)
    /// and opens a session.
    /// </summary>
    public sealed class EndpointFileSystemFactory(
        IBackupFileSystem localFileSystem,
        IConnectionResolver connectionResolver) : IEndpointFileSystemFactory
    {
        private static readonly IDisposable NoSession = new NoopDisposable();

        public async Task<EndpointFileSystem> ResolveAsync(int? connectionId, string configuredPath, CancellationToken cancellationToken = default)
        {
            if (connectionId is not { } id)
            {
                return new EndpointFileSystem(localFileSystem, configuredPath, NoSession);
            }

            var type = await connectionResolver.GetTypeAsync(id, cancellationToken);
            switch (type)
            {
                case ConnectionType.GoogleDrive:
                {
                    var info = await connectionResolver.GetGoogleDriveInfoAsync(id, cancellationToken);
                    var drive = GoogleDriveBackupFileSystem.Connect(info);
                    var basePath = CombineRelative(info.RootFolder, configuredPath);
                    return new EndpointFileSystem(drive, basePath, drive);
                }

                case ConnectionType.Smb:
                default:
                {
                    var info = await connectionResolver.GetSmbInfoAsync(id, cancellationToken);
                    var smb = SmbBackupFileSystem.Connect(info);
                    var basePath = CombineRelative(info.RootFolder, configuredPath);
                    return new EndpointFileSystem(smb, basePath, smb);
                }
            }
        }

        // Joins two share-relative path fragments with a single backslash, trimming separators.
        private static string CombineRelative(string? root, string? path)
        {
            var left = (root ?? string.Empty).Replace('/', '\\').Trim('\\');
            var right = (path ?? string.Empty).Replace('/', '\\').Trim('\\');
            if (left.Length == 0)
            {
                return right;
            }
            return right.Length == 0 ? left : $@"{left}\{right}";
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
