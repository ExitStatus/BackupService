using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;

namespace BackupService.FileSystem
{
    /// <summary>
    /// Default <see cref="IBackupFileSystem"/> — a thin pass-through to <see cref="System.IO"/>.
    /// Registered as a singleton; not unit-tested itself (real-IO, like <see cref="FolderBrowser"/>).
    /// </summary>
    public sealed class BackupFileSystem : IBackupFileSystem
    {
        private const int CompareBufferSize = 64 * 1024;

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

        public bool FileExists(string path) => File.Exists(path);

        public IReadOnlyList<string> GetFiles(string directory) => Directory.GetFiles(directory);

        public IReadOnlyList<string> GetDirectories(string directory) => Directory.GetDirectories(directory);

        public bool IsDirectoryLink(string path)
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }

        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public long GetFileSize(string path) => LengthOf(new FileInfo(path));

        public FileStat GetFileStat(string path)
        {
            // One stat: FileInfo caches the attributes it reads for LastWriteTimeUtc, so Length costs nothing extra.
            var info = new FileInfo(path);
            return new FileStat(info.LastWriteTimeUtc, LengthOf(info));
        }

        // A file symlink reports its own length (0), but OpenRead follows the link and reads the target — so report
        // the final target's length, or a copy of it would never match its source's size. The reparse-point
        // attribute comes from the cached stat, so ordinary files pay nothing for the check.
        private static long LengthOf(FileInfo info)
        {
            if (info.Exists
                && (info.Attributes & FileAttributes.ReparsePoint) != 0
                && info.LinkTarget is not null
                && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            {
                return target.Length;
            }
            return info.Length;
        }

        public void SetLastWriteTimeUtc(string path, DateTime value) => File.SetLastWriteTimeUtc(path, value);

        public Stream OpenRead(string path) =>
            // Permissive share so a file another process holds open for writing can still be read.
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        public Stream OpenWrite(string path) =>
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        public void CopyFile(string source, string destination, bool overwrite)
        {
            // Open the source with a permissive share mode so a file another process holds open for
            // writing (logs, indexes, etc.) can still be read — File.Copy uses only FileShare.Read and
            // fails on those. A naive stream copy doesn't carry the source's last-write-time across, so
            // re-stamp it afterwards: the sync engine compares LastWriteTimeUtc to decide copy/skip, and
            // a "now" timestamp would make every later run treat the destination as newer.
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        }

        public void MoveFile(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        public void DeleteFile(string path) => File.Delete(path);

        public string GetTempFilePath(string fileName)
        {
            var directory = Path.Combine(Path.GetTempPath(), "BackupService", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, CompressionLevel compressionLevel = CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true, Action<string>? onEntryProcessed = null)
        {
            // A file that fails part-way through being read has already started its entry, and an entry can't be
            // taken back out of an archive being written — left in, it's a truncated file the log calls "skipped".
            // So the archive is rebuilt without it. That's rare: almost every unreadable file fails when it's opened,
            // before its entry is started, and is simply skipped.
            var leftOut = new HashSet<string>(StringComparer.Ordinal);
            var skippedMidway = new List<ZipSkippedFile>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            bool Include(string entry) => !leftOut.Contains(entry) && (includeEntry is null || includeEntry(entry));
            void Processed(string entry)
            {
                if (reported.Add(entry))
                {
                    onEntryProcessed?.Invoke(entry); // once per file, however many times the archive is built
                }
            }

            while (true)
            {
                try
                {
                    // Encrypted archives need a ZIP writer that supports encryption (the BCL can't), so route them
                    // through SharpZipLib; the common unencrypted path stays on System.IO.Compression unchanged.
                    var built = string.IsNullOrEmpty(password)
                        ? CreatePlainZip(sourceDirectory, destinationZip, includeSubfolders, Include, comment, compressionLevel, Processed)
                        : CreateEncryptedZip(sourceDirectory, destinationZip, includeSubfolders, Include, comment, compressionLevel, password, useAesEncryption, Processed);
                    return new ZipBuildResult(built.Added, [.. skippedMidway, .. built.Skipped]);
                }
                catch (ReadFailedMidEntryException ex)
                {
                    leftOut.Add(ex.EntryName);
                    skippedMidway.Add(new ZipSkippedFile(ex.EntryName, ex.InnerException?.Message ?? ex.Message));
                    Processed(ex.EntryName); // it's done with (skipped), as far as progress is concerned
                    File.Delete(destinationZip); // start again from scratch
                }
            }
        }

        // Thrown when a source file fails while its entry is being written (see CreateZipFromDirectory).
        private sealed class ReadFailedMidEntryException(string entryName, Exception inner)
            : IOException($"Reading '{entryName}' failed part-way through.", inner)
        {
            public string EntryName { get; } = entryName;
        }

        // Copies a source file into its (already started) entry. A READ failure is turned into
        // ReadFailedMidEntryException so the archive is rebuilt without the file; a WRITE failure (e.g. the temp disk
        // filling up) is left as-is and fails the archive.
        private static void CopyIntoEntry(Stream source, Stream entry, string entryName)
        {
            var buffer = new byte[81920];
            while (true)
            {
                int read;
                try
                {
                    read = source.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ReadFailedMidEntryException(entryName, ex);
                }
                if (read == 0)
                {
                    return;
                }
                entry.Write(buffer, 0, read);
            }
        }

        // Opens a source file to archive, or returns null (with the reason) when it can't be — before any entry exists.
        private static FileStream? TryOpenSource(string file, out string? reason)
        {
            try
            {
                // Permissive share mode reads files other processes hold open for writing (the FileShare.Read that
                // CreateEntryFromFile uses would fail on those).
                reason = null;
                return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = ex.Message;
                return null;
            }
        }

        private static ZipBuildResult CreatePlainZip(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry, string? comment, CompressionLevel compressionLevel, Action<string>? onEntryProcessed)
        {
            // Build the archive entry-by-entry (rather than ZipFile.CreateFromDirectory) so the caller
            // gets the list of files added — both for the top-level-only case and for verbose logging —
            // and so one unreadable file is skipped rather than aborting the whole archive.
            var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var added = new List<string>();
            var skipped = new List<ZipSkippedFile>();

            using var zip = System.IO.Compression.ZipFile.Open(destinationZip, ZipArchiveMode.Create);
            if (comment is not null)
            {
                zip.Comment = comment; // stored in the EOCD record (the "only copy on change" fingerprint)
            }
            foreach (var file in Directory.GetFiles(sourceDirectory, "*", searchOption))
            {
                var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/'); // zip-standard separators
                if (includeEntry is not null && !includeEntry(entryName))
                {
                    continue; // filtered out by include/exclude rules — omitted, not an error
                }
                using (var src = TryOpenSource(file, out var reason))
                {
                    if (src is null)
                    {
                        skipped.Add(new ZipSkippedFile(entryName, reason!));
                    }
                    else
                    {
                        var entry = zip.CreateEntry(entryName, compressionLevel);
                        entry.LastWriteTime = File.GetLastWriteTime(file); // mirror CreateEntryFromFile
                        using var entryStream = entry.Open();
                        CopyIntoEntry(src, entryStream, entryName);
                        added.Add(entryName);
                    }
                }
                onEntryProcessed?.Invoke(entryName);
            }

            return new ZipBuildResult(added, skipped);
        }

        // Encrypted counterpart of CreatePlainZip (SharpZipLib): same per-entry skip-on-locked, includeEntry
        // filtering, comment and timestamps, but each entry is encrypted (AES-256, else legacy ZipCrypto).
        private static ZipBuildResult CreateEncryptedZip(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry, string? comment, CompressionLevel compressionLevel, string password, bool useAesEncryption, Action<string>? onEntryProcessed)
        {
            var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var added = new List<string>();
            var skipped = new List<ZipSkippedFile>();
            var stored = compressionLevel == CompressionLevel.NoCompression;

            using var output = new FileStream(destinationZip, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipOutputStream(output) { Password = password, IsStreamOwner = true };
            zip.SetLevel(ToDeflateLevel(compressionLevel));
            if (comment is not null)
            {
                zip.SetComment(comment); // EOCD comment — the "only copy on change" fingerprint
            }

            foreach (var file in Directory.GetFiles(sourceDirectory, "*", searchOption))
            {
                var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                if (includeEntry is not null && !includeEntry(entryName))
                {
                    continue;
                }
                // Open first (the dominant failure point) so a locked file is skipped before an entry is written —
                // matching the plain path.
                using (var src = TryOpenSource(file, out var reason))
                {
                    if (src is null)
                    {
                        skipped.Add(new ZipSkippedFile(entryName, reason!));
                    }
                    else
                    {
                        var entry = new ZipEntry(entryName)
                        {
                            DateTime = File.GetLastWriteTime(file),
                            AESKeySize = useAesEncryption ? 256 : 0, // 0 with a Password set = legacy ZipCrypto
                        };
                        if (stored)
                        {
                            entry.CompressionMethod = CompressionMethod.Stored;
                        }
                        zip.PutNextEntry(entry);
                        CopyIntoEntry(src, zip, entryName);
                        zip.CloseEntry();
                        added.Add(entryName);
                    }
                }
                onEntryProcessed?.Invoke(entryName);
            }

            zip.Finish();
            return new ZipBuildResult(added, skipped);
        }

        // BCL CompressionLevel → SharpZipLib deflate level (0–9).
        private static int ToDeflateLevel(CompressionLevel level) => level switch
        {
            CompressionLevel.NoCompression => 0,
            CompressionLevel.Fastest => 1,
            CompressionLevel.SmallestSize => 9,
            _ => 6, // Optimal
        };

        public string? GetZipComment(string path)
        {
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                return string.IsNullOrEmpty(zip.Comment) ? null : zip.Comment;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return null; // not a readable ZIP — treat as "no fingerprint" so the caller rebuilds
            }
        }

        public bool FilesContentEqual(string a, string b)
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
            {
                return false;
            }

            using var streamA = infoA.OpenRead();
            using var streamB = infoB.OpenRead();
            var bufferA = new byte[CompareBufferSize];
            var bufferB = new byte[CompareBufferSize];

            while (true)
            {
                var readA = ReadBlock(streamA, bufferA);
                var readB = ReadBlock(streamB, bufferB);
                if (readA != readB)
                {
                    return false;
                }
                if (readA == 0)
                {
                    return true;
                }
                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }

        // Reads up to a full buffer, tolerating partial reads; returns the count read (0 at EOF).
        private static int ReadBlock(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }
    }
}
