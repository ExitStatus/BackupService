using BackupService.Connections;
using BackupService.Connections.Usb;
using BackupService.Enumerations;
using BackupService.FileSystem.GoogleDrive;
using BackupService.FileSystem.Smb;

namespace BackupService.FileSystem
{
    /// <summary>
    /// Default <see cref="IEndpointFileSystemFactory"/>: a null connection id resolves to the shared local
    /// filesystem; a set id resolves the connection by type (SMB, Google Drive or USB, decrypting any secrets)
    /// and opens a session. A USB connection resolves to the <b>local</b> filesystem rooted at the bound device's
    /// current drive letter, so it's only resolvable while the device is connected.
    /// </summary>
    public sealed class EndpointFileSystemFactory(
        IBackupFileSystem localFileSystem,
        IConnectionResolver connectionResolver,
        IUsbConnector usbConnector,
        ILoggerFactory loggerFactory) : IEndpointFileSystemFactory
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

                case ConnectionType.Usb:
                {
                    var info = await connectionResolver.GetUsbInfoAsync(id, cancellationToken);
                    if (info.Kind == UsbDeviceKind.Mtp)
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new PlatformNotSupportedException("MTP devices are only supported on Windows.");
                        }

                        // MediaDevices uses device-absolute backslash paths (e.g. "\Internal storage\DCIM").
                        var mtpPath = CombineDeviceAbsolute(info.RootFolder, configuredPath);
                        var mtpLogger = loggerFactory.CreateLogger<Mtp.MtpBackupFileSystem>();
                        var mtp = Mtp.MtpBackupFileSystem.Connect(info, mtpLogger); // throws when the device isn't connected
                        return new EndpointFileSystem(mtp, mtpPath, mtp);
                    }

                    var mountPath = usbConnector.FindMountPath(info)
                        ?? throw new InvalidOperationException($"The USB device for connection {id} is not connected.");
                    var relative = CombineRelative(info.RootFolder, configuredPath);
                    var basePath = relative.Length == 0 ? mountPath : Path.Combine(mountPath, relative);
                    return new EndpointFileSystem(localFileSystem, basePath, NoSession);
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

        // Joins an MTP root + relative path into a device-absolute path (leading backslash preserved).
        private static string CombineDeviceAbsolute(string? root, string? path)
        {
            var relative = CombineRelative(root, path);
            return relative.Length == 0 ? @"\" : $@"\{relative}";
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
