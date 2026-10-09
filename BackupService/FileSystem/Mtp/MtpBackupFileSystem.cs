using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BackupService.Connections.Usb;
using MediaDevices;
using MetadataExtractor.Formats.Exif;

namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// Read-only <see cref="IBackupFileSystem"/> over an MTP/PTP device via the MediaDevices (WPD) library — for using
    /// a camera/phone as a backup <b>source</b>. Modeled on <see cref="GoogleDrive.GoogleDriveBackupFileSystem"/>: the
    /// engine works in backslash device-absolute paths, which pass straight to MediaDevices. <see cref="OpenRead"/>
    /// downloads to a self-deleting local temp. Every <b>write</b> member throws — USB connections are source-only.
    /// Windows-only; created by <see cref="EndpointFileSystemFactory"/> per run and disposed (disconnecting the device).
    /// <para>
    /// MTP/WPD sessions are flaky — many cameras (Sony bodies in particular) drop the session after a handful of
    /// transfers even though the device stays physically plugged in, surfacing as a <c>NotConnectedException</c>
    /// mid-copy. Every device call therefore goes through <see cref="WithRetry{T}"/>, which transparently re-acquires
    /// and reconnects the device (by its serial) and retries, so a dropped session no longer fails the run.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class MtpBackupFileSystem : IBackupFileSystem, IDisposable
    {
        private const int MaxAttempts = 4;
        private const int ReconnectDelayMs = 400;

        // How much of a file to read when falling back to EXIF for the date: EXIF metadata sits at the very start of
        // a camera raw/JPEG, so reading ~1 MB captures it without transferring the whole multi-MB file (a raw can be
        // 25-60 MB). If the date isn't found in this window (rare) we fall back to the whole file.
        private const int ExifHeaderBytes = 1024 * 1024;

        private readonly string _serial;
        private readonly ILogger? _logger;
        private MediaDevice? _device;
        private bool _disposed;

        private MtpBackupFileSystem(string serial, MediaDevice device, ILogger? logger)
        {
            _serial = serial;
            _device = device;
            _logger = logger;
        }

        /// <summary>Finds the device by its stored MTP serial (the WPD DeviceId) and connects it. Throws if absent.</summary>
        public static MtpBackupFileSystem Connect(UsbConnectionInfo info, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(info.MtpSerial))
            {
                throw new InvalidOperationException("The USB connection has no MTP device serial.");
            }

            var device = FindDevice(info.MtpSerial)
                ?? throw new InvalidOperationException("The MTP device for this connection is not connected.");

            device.Connect();
            return new MtpBackupFileSystem(info.MtpSerial, device, logger);
        }

        // ---- read members (the engine only reads a source) ----

        public bool DirectoryExists(string path) => WithRetry(() =>
        {
            try { return _device!.DirectoryExists(NormalizeOrRoot(path)); }
            catch (Exception ex) when (!IsDisconnect(ex)) { return false; }
        });

        public bool FileExists(string path) => WithRetry(() =>
        {
            try { return _device!.FileExists(Normalize(path)); }
            catch (Exception ex) when (!IsDisconnect(ex)) { return false; }
        });

        public IReadOnlyList<string> GetFiles(string directory) =>
            WithRetry(() => _device!.GetFiles(NormalizeOrRoot(directory)).ToList());

        public IReadOnlyList<string> GetDirectories(string directory) =>
            WithRetry(() => _device!.GetDirectories(NormalizeOrRoot(directory)).ToList());

        public DateTime GetLastWriteTimeUtc(string path) =>
            ResolveWriteTimeUtc(WithRetry(() => _device!.GetFileInfo(Normalize(path))), path);

        public long GetFileSize(string path) =>
            WithRetry(() => MtpDownloadCheck.ToReportedSize(_device!.GetFileInfo(Normalize(path)).Length));

        public FileStat GetFileStat(string path)
        {
            // One path lookup for both (each lookup lists every sibling in every folder on the way down).
            var info = WithRetry(() => _device!.GetFileInfo(Normalize(path)));
            return new FileStat(ResolveWriteTimeUtc(info, path), MtpDownloadCheck.ToReportedSize(info.Length));
        }

        private DateTime ResolveWriteTimeUtc(MediaFileInfo info, string path)
        {
            // 1) WPD modified date, 2) WPD date-authored (what Explorer shows as "Date Picture Taken"), 3) WPD
            // created date. Many cameras (e.g. Sony bodies) expose none of these via MediaDevices for RAW files.
            var wpd = info.LastWriteTime ?? info.DateAuthored ?? info.CreationTime;
            if (wpd is { } t && t != DateTime.MinValue)
            {
                // MTP times are device-local/unspecified; normalise to UTC consistently so a round-trip compares equal.
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }

            // 3b) Read the WPD object date directly from the property store (a true property read, no file transfer).
            // MediaDevices misses some cameras' dates (e.g. a Sony exposing only DATE_CREATED, as a VT_DATE), so we
            // enumerate the property store ourselves and handle both the numeric and string forms.
            if (WithRetry(() => MtpObjectDate.TryRead(_device!, info)) is { } objectDate)
            {
                return objectDate;
            }

            // 4) Last resort: the EXIF "date taken" embedded in the file. EXIF lives at the start of the file, so
            // read just the header bytes off the device's (forward-only) stream rather than downloading the whole
            // file — that keeps an unchanged-file re-check cheap instead of transferring tens of MB per photo.
            var puid = info.PersistentUniqueId;
            if (!string.IsNullOrEmpty(puid))
            {
                var header = WithRetry(() => ReadHeaderBytes(puid, ExifHeaderBytes));
                if (ParseExifDateTakenUtc(header) is { } headerDate)
                {
                    return headerDate;
                }
            }

            // Rare: no WPD date and the EXIF wasn't in the header window — fall back to the whole file (slow). Only for
            // a file that can hold EXIF at all: a video never does, so this downloaded every video in full, on every
            // run, just to find no date.
            if (!ExifFiles.MayHoldExif(path))
            {
                return DateTime.MinValue;
            }
            _logger?.LogWarning("MTP: no WPD or header-EXIF date for '{File}'; downloading the whole file to read its date.",
                DisplayName(path));
            using var full = OpenRead(path);
            return ParseExifDateTakenUtc(full) ?? DateTime.MinValue;
        }

        public Stream OpenRead(string path) => OpenRead(path, CancellationToken.None);

        public Stream OpenRead(string path, CancellationToken cancellationToken)
        {
            var tempPath = DownloadToTemp(path, cancellationToken);
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

        public bool IsReadOnly => true;

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
                _device?.Disconnect();
            }
            catch
            {
                // best effort
            }
            _device?.Dispose();
            _device = null;
        }

        // Runs a device operation, transparently reconnecting (re-acquiring the device by serial) and retrying when
        // the WPD session has dropped — the common MTP failure where the device stays plugged in but reports
        // "Not connected" after some activity. When the device is genuinely gone (switched off / unplugged), or it
        // stops answering altogether, this throws EndpointUnavailableException so the run aborts fast and clean rather
        // than churning the same dead device for every remaining file. A device that still answers after the retries
        // are used up is refusing just this object, so only this file fails (see MtpRetryOutcome).
        private T WithRetry<T>(Func<T> op, CancellationToken cancellationToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    EnsureConnected();
                    return op();
                }
                catch (EndpointUnavailableException)
                {
                    throw; // device confirmed gone — don't retry
                }
                catch (Exception ex) when (IsDisconnect(ex))
                {
                    if (attempt >= MaxAttempts)
                    {
                        throw MtpRetryOutcome.ForExhaustedRetries(ex, DeviceStillAnswers(), _serial);
                    }

                    InvalidateDevice(); // drop the stale handle; the next EnsureConnected re-acquires or declares it gone
                    Pause(cancellationToken);
                }
            }
        }

        // Whether the device answers a simple request (listing its storage) on a fresh session.
        private bool DeviceStillAnswers()
        {
            try
            {
                InvalidateDevice();
                EnsureConnected();
                _ = _device!.GetDirectories(@"\").ToList();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Waits out the reconnect delay, returning early (and throwing) if the run is stopped meanwhile.
        private static void Pause(CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne(ReconnectDelayMs);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Ensures a live session, re-acquiring a fresh device handle for the same physical device when needed. A
        // fresh MediaDevice from GetDevices() recovers cases where the old session object is unusable, not just
        // disconnected; if the device can no longer be found it's gone — surface that as fatal to the run.
        private void EnsureConnected()
        {
            if (_device is { IsConnected: true })
            {
                return;
            }

            try { _device?.Disconnect(); } catch { /* best effort */ }
            try { _device?.Dispose(); } catch { /* best effort */ }

            _device = FindDevice(_serial)
                ?? throw new EndpointUnavailableException($"The MTP device '{_serial}' was disconnected.");
            _device.Connect();
        }

        private void InvalidateDevice()
        {
            try { _device?.Disconnect(); } catch { /* best effort */ }
            try { _device?.Dispose(); } catch { /* best effort */ }
            _device = null;
        }

        // Starts a fresh WPD session on the same device handle. Unlike InvalidateDevice this doesn't re-enumerate
        // devices, so one that is briefly missing from the device list mid-reset isn't mistaken for one that was
        // unplugged (which would abort the whole run). If the handle won't reconnect, drop it so the next call
        // re-acquires the device by serial as usual.
        private void ResetSession()
        {
            try
            {
                _device?.Disconnect();
                _device?.Connect();
            }
            catch
            {
                InvalidateDevice();
            }
        }

        private static NotSupportedException ReadOnly() =>
            new("Writing to an MTP device is not supported — USB connections are source-only.");

        private static bool IsDisconnect(Exception ex) =>
            ex is NotConnectedException
            || ex is COMException
            || (ex.Message?.Contains("not connected", StringComparison.OrdinalIgnoreCase) ?? false);

        // Caller owns/disposes the returned device.
        private static MediaDevice? FindDevice(string serial)
        {
            MediaDevice? match = null;
            foreach (var device in MediaDevice.GetDevices())
            {
                if (match is null && string.Equals(device.DeviceId, serial, StringComparison.OrdinalIgnoreCase))
                {
                    match = device;
                }
                else
                {
                    device.Dispose();
                }
            }

            return match;
        }

        // The trailing segment of a device-absolute path, for concise per-file diagnostic logging.
        private static string DisplayName(string path)
        {
            var normalized = Normalize(path).TrimEnd('\\');
            var index = normalized.LastIndexOf('\\');
            return index < 0 ? normalized : normalized[(index + 1)..];
        }

        private static string Normalize(string path) => (path ?? string.Empty).Replace('/', '\\');

        private static string NormalizeOrRoot(string path)
        {
            var normalized = Normalize(path).TrimEnd('\\');
            return string.IsNullOrEmpty(normalized) ? @"\" : normalized;
        }

        // Downloads the device file to a fresh local temp (with reconnect/retry). Caller owns the returned path.
        // A WPD transfer can end early WITHOUT throwing (the device's stream just reports end-of-data when the session
        // hiccups), which silently produced truncated/corrupt copies. So the download is verified against the size the
        // device reports for the object (see MtpDownloadCheck): a SHORT download is discarded and re-fetched on a fresh
        // session, and if it never completes the read fails (a per-file error) rather than returning partial data. A
        // download at least as large as reported, or one with no usable reported size, is accepted.
        // The token is checked between every chunk written, so a Stop interrupts even a multi-GB transfer.
        private string DownloadToTemp(string path, CancellationToken cancellationToken)
        {
            var tempPath = LocalTempPath();
            try
            {
                long? previousActual = null;
                for (var attempt = 1; ; attempt++)
                {
                    // Resolve the object once per attempt: its reported size is what the download is checked against,
                    // and its persistent id lets the transfer skip a second path walk.
                    var (persistentId, expected) = WithRetry(() =>
                    {
                        var info = _device!.GetFileInfo(Normalize(path));
                        return (info.PersistentUniqueId, MtpDownloadCheck.ToReportedSize(info.Length));
                    }, cancellationToken);

                    var actual = WithRetry(() =>
                    {
                        // Re-truncate the temp on a retry so a partially-downloaded file from a dropped session is discarded.
                        using var output = new CancellableCountingStream(System.IO.File.Create(tempPath), cancellationToken);
                        if (string.IsNullOrEmpty(persistentId))
                        {
                            _device!.DownloadFile(Normalize(path), output);
                        }
                        else
                        {
                            _device!.DownloadFileFromPersistentUniqueId(persistentId, output);
                        }
                        output.Flush();
                        return output.BytesWritten;
                    }, cancellationToken);

                    if (MtpDownloadCheck.IsComplete(expected, actual))
                    {
                        break;
                    }

                    _logger?.LogWarning(
                        "MTP: incomplete download of '{File}' ({Actual} of {Expected} bytes, attempt {Attempt}/{MaxAttempts}).",
                        DisplayName(path), actual, expected, attempt, MaxAttempts);

                    if (!MtpDownloadCheck.ShouldRetry(actual, previousActual, attempt, MaxAttempts))
                    {
                        throw new IOException(actual == previousActual
                            ? $"Incomplete transfer from the MTP device: it reports {expected} bytes but delivered only {actual} on consecutive attempts."
                            : $"Incomplete transfer from the MTP device: received {actual} of {expected} bytes after {attempt} attempts.");
                    }

                    // Start the next attempt on a fresh session — a truncated transfer usually means the session is unwell.
                    previousActual = actual;
                    ResetSession();
                    Pause(cancellationToken);
                }
            }
            catch
            {
                // Don't leave a partial download behind when the transfer fails (e.g. the device was switched off).
                try { System.IO.File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }
            return tempPath;
        }

        // Reads up to maxBytes from the start of the device file via its forward-only WPD stream. Used to grab just
        // the EXIF header without transferring the whole (multi-MB) file.
        private byte[] ReadHeaderBytes(string persistentUniqueId, int maxBytes)
        {
            using var stream = _device!.OpenReadFromPersistentUniqueId(persistentUniqueId);
            using var buffer = new MemoryStream(Math.Min(maxBytes, 1 << 20));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while (total < maxBytes
                   && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes - total))) > 0)
            {
                buffer.Write(chunk, 0, read);
                total += read;
            }
            return buffer.ToArray();
        }

        private static DateTime? ParseExifDateTakenUtc(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return ParseExifDateTakenUtc(stream);
        }

        // Reads the EXIF "date taken" (DateTimeOriginal, else the IFD0 DateTime) from an image/raw stream. Returned
        // as UTC-kind wall-clock to match the WPD-date handling (consistent round-trip with the stamped copy).
        private static DateTime? ParseExifDateTakenUtc(Stream stream)
        {
            try
            {
                var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(stream);

                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd is not null
                    && MetadataExtractor.DirectoryExtensions.TryGetDateTime(subIfd, ExifDirectoryBase.TagDateTimeOriginal, out var taken))
                {
                    return DateTime.SpecifyKind(taken, DateTimeKind.Utc);
                }

                var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
                if (ifd0 is not null
                    && MetadataExtractor.DirectoryExtensions.TryGetDateTime(ifd0, ExifDirectoryBase.TagDateTime, out var modified))
                {
                    return DateTime.SpecifyKind(modified, DateTimeKind.Utc);
                }

                return null;
            }
            catch
            {
                // Not an image we can read, no EXIF, or the date sat beyond a truncated header window — no date.
                return null;
            }
        }

        private static string LocalTempPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "BackupService", "mtp");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        }

        // The download's destination: forwards writes to the temp file, counts the bytes, and throws once the run is
        // stopped. MediaDevices copies the whole object in one blocking call, so checking the token on each chunk it
        // writes is the only way to interrupt a large transfer part-way.
        private sealed class CancellableCountingStream(Stream inner, CancellationToken cancellationToken) : Stream
        {
            public long BytesWritten { get; private set; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inner.Write(buffer);
                BytesWritten += buffer.Length;
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
