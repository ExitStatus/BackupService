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

        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public long GetFileSize(string path) => new FileInfo(path).Length;

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

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null, string? comment = null, CompressionLevel compressionLevel = CompressionLevel.Optimal, string? password = null, bool useAesEncryption = true)
        {
            // Encrypted archives need a ZIP writer that supports encryption (the BCL can't), so route them
            // through SharpZipLib; the common unencrypted path stays on System.IO.Compression unchanged.
            return string.IsNullOrEmpty(password)
                ? CreatePlainZip(sourceDirectory, destinationZip, includeSubfolders, includeEntry, comment, compressionLevel)
                : CreateEncryptedZip(sourceDirectory, destinationZip, includeSubfolders, includeEntry, comment, compressionLevel, password, useAesEncryption);
        }

        private static ZipBuildResult CreatePlainZip(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry, string? comment, CompressionLevel compressionLevel)
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
                try
                {
                    // Permissive share mode reads files other processes hold open for writing (the
                    // FileShare.Read that CreateEntryFromFile uses would fail on those). The open is the
                    // dominant failure point; a file readable here is added in full.
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(entryName, compressionLevel);
                    entry.LastWriteTime = File.GetLastWriteTime(file); // mirror CreateEntryFromFile
                    using var entryStream = entry.Open();
                    src.CopyTo(entryStream);
                    added.Add(entryName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new ZipSkippedFile(entryName, ex.Message));
                }
            }

            return new ZipBuildResult(added, skipped);
        }

        // Encrypted counterpart of CreatePlainZip (SharpZipLib): same per-entry skip-on-locked, includeEntry
        // filtering, comment and timestamps, but each entry is encrypted (AES-256, else legacy ZipCrypto).
        private static ZipBuildResult CreateEncryptedZip(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry, string? comment, CompressionLevel compressionLevel, string password, bool useAesEncryption)
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
                try
                {
                    // Open first (the dominant failure point) so a locked file is skipped before an entry
                    // is written — matching the plain path.
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                    src.CopyTo(zip);
                    zip.CloseEntry();
                    added.Add(entryName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new ZipSkippedFile(entryName, ex.Message));
                }
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
