using System.IO.Compression;
using System.Runtime.Versioning;
using BackupService.Connections.Usb;
using MediaDevices;

namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// Read-only <see cref="IBackupFileSystem"/> over an MTP/PTP device via the MediaDevices (WPD) library — for using
    /// a camera/phone as a backup <b>source</b>. Modeled on <see cref="GoogleDrive.GoogleDriveBackupFileSystem"/>: the
    /// engine works in backslash device-absolute paths, which pass straight to MediaDevices. <see cref="OpenRead"/>
    /// downloads to a self-deleting local temp. Every <b>write</b> member throws — USB connections are source-only.
    /// Windows-only; created by <see cref="EndpointFileSystemFactory"/> per run and disposed (disconnecting the device).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class MtpBackupFileSystem : IBackupFileSystem, IDisposable
    {
        private readonly MediaDevice _device;
        private bool _disposed;

        private MtpBackupFileSystem(MediaDevice device) => _device = device;

        /// <summary>Finds the device by its stored MTP serial (the WPD DeviceId) and connects it. Throws if absent.</summary>
        public static MtpBackupFileSystem Connect(UsbConnectionInfo info)
        {
            if (string.IsNullOrEmpty(info.MtpSerial))
            {
                throw new InvalidOperationException("The USB connection has no MTP device serial.");
            }

            MediaDevice? device = null;
            foreach (var candidate in MediaDevice.GetDevices())
            {
                if (device is null && string.Equals(candidate.DeviceId, info.MtpSerial, StringComparison.OrdinalIgnoreCase))
                {
                    device = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            if (device is null)
            {
                throw new InvalidOperationException("The MTP device for this connection is not connected.");
            }

            device.Connect();
            return new MtpBackupFileSystem(device);
        }

        // ---- read members (the engine only reads a source) ----

        public bool DirectoryExists(string path)
        {
            try
            {
                return _device.DirectoryExists(NormalizeOrRoot(path));
            }
            catch
            {
                return false;
            }
        }

        public bool FileExists(string path)
        {
            try
            {
                return _device.FileExists(Normalize(path));
            }
            catch
            {
                return false;
            }
        }

        public IReadOnlyList<string> GetFiles(string directory) =>
            _device.GetFiles(NormalizeOrRoot(directory)).ToList();

        public IReadOnlyList<string> GetDirectories(string directory) =>
            _device.GetDirectories(NormalizeOrRoot(directory)).ToList();

        public DateTime GetLastWriteTimeUtc(string path)
        {
            var info = _device.GetFileInfo(Normalize(path));
            var time = info.LastWriteTime ?? info.CreationTime ?? DateTime.MinValue;
            // MTP times are device-local/unspecified; normalise to UTC consistently so a round-trip compares equal.
            return time == DateTime.MinValue ? DateTime.MinValue : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        public long GetFileSize(string path) => (long)_device.GetFileInfo(Normalize(path)).Length;

        public Stream OpenRead(string path)
        {
            var tempPath = LocalTempPath();
            using (var fileStream = System.IO.File.Create(tempPath))
            {
                _device.DownloadFile(Normalize(path), fileStream);
            }
            // The OS removes the temp when the returned stream is disposed.
            return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }

        public bool FilesContentEqual(string a, string b)
        {
            using var streamA = OpenRead(a);
            using var streamB = OpenRead(b);
            return StreamCompare.Equal(streamA, streamB);
        }

        // ---- write/archive members: unsupported (USB is source-only / read-only) ----

        public void CreateDirectory(string path) => throw ReadOnly();
        public void DeleteDirectory(string path, bool recursive) => throw ReadOnly();
        public void SetLastWriteTimeUtc(string path, DateTime value) => throw ReadOnly();
        public Stream OpenWrite(string path) => throw ReadOnly();
        public void CopyFile(string source, string destination, bool overwrite) => throw ReadOnly();
        public void MoveFile(string source, string destination, bool overwrite) => throw ReadOnly();
        public void DeleteFile(string path) => throw ReadOnly();

        public string GetTempFilePath(string fileName) =>
            throw new NotSupportedException("Temp files are local-only.");

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, CompressionLevel compressionLevel = CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null) =>
            throw new NotSupportedException("Zipping from an MTP source is not supported (it is staged locally first).");

        public string? GetZipComment(string path) => null;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _device.Disconnect();
            }
            catch
            {
                // best effort
            }
            _device.Dispose();
        }

        private static NotSupportedException ReadOnly() =>
            new("Writing to an MTP device is not supported — USB connections are source-only.");

        private static string Normalize(string path) => (path ?? string.Empty).Replace('/', '\\');

        private static string NormalizeOrRoot(string path)
        {
            var normalized = Normalize(path).TrimEnd('\\');
            return string.IsNullOrEmpty(normalized) ? @"\" : normalized;
        }

        private static string LocalTempPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "BackupService", "mtp");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        }
    }
}
