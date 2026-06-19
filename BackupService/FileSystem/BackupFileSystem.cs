using System.IO;
using System.IO.Compression;

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

        public void SetLastWriteTimeUtc(string path, DateTime value) => File.SetLastWriteTimeUtc(path, value);

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

        public ZipBuildResult CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders, Func<string, bool>? includeEntry = null)
        {
            // Build the archive entry-by-entry (rather than ZipFile.CreateFromDirectory) so the caller
            // gets the list of files added — both for the top-level-only case and for verbose logging —
            // and so one unreadable file is skipped rather than aborting the whole archive.
            var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var added = new List<string>();
            var skipped = new List<ZipSkippedFile>();

            using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
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
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
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
