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

        public void CopyFile(string source, string destination, bool overwrite) =>
            File.Copy(source, destination, overwrite);

        public void MoveFile(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        public void DeleteFile(string path) => File.Delete(path);

        public string GetTempFilePath(string fileName)
        {
            var directory = Path.Combine(Path.GetTempPath(), "BackupService", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }

        public IReadOnlyList<string> CreateZipFromDirectory(string sourceDirectory, string destinationZip, bool includeSubfolders)
        {
            // Build the archive entry-by-entry (rather than ZipFile.CreateFromDirectory) so the caller
            // gets the list of files added — both for the top-level-only case and for verbose logging.
            var searchOption = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<string>();

            using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
            foreach (var file in Directory.GetFiles(sourceDirectory, "*", searchOption))
            {
                var entryName = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/'); // zip-standard separators
                zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                entries.Add(entryName);
            }

            return entries;
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
