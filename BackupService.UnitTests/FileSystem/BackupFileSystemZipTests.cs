using BackupService.FileSystem;
using FluentAssertions;
using SharpZip = ICSharpCode.SharpZipLib.Zip;

namespace BackupService.UnitTests.FileSystem
{
    /// <summary>
    /// Real-IO round-trip tests for the encrypted-archive path of <see cref="BackupFileSystem"/> (the rest of
    /// the class is a thin System.IO pass-through, exercised via the engine fakes). Encryption is
    /// security-sensitive and otherwise untested, so verify the produced ZIP genuinely needs the password.
    /// </summary>
    [TestFixture]
    public class BackupFileSystemZipTests
    {
        private const string EntryName = "file.txt";
        private const string Content = "secret contents";

        private string _dir = null!;
        private string _source = null!;
        private string _zip = null!;
        private BackupFileSystem _fs = null!;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "BackupServiceTests", Guid.NewGuid().ToString("N"));
            _source = Path.Combine(_dir, "src");
            Directory.CreateDirectory(_source);
            File.WriteAllText(Path.Combine(_source, EntryName), Content);
            _zip = Path.Combine(_dir, "out.zip");
            _fs = new BackupFileSystem();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        [Test]
        public void Aes_EncryptedArchive_RoundTripsWithCorrectPassword()
        {
            var result = _fs.CreateZipFromDirectory(_source, _zip, includeSubfolders: false, password: "hunter2", useAesEncryption: true);
            result.Added.Should().ContainSingle().Which.Should().Be(EntryName);

            using var zf = new SharpZip.ZipFile(_zip) { Password = "hunter2" };
            var entry = zf.GetEntry(EntryName);
            entry.Should().NotBeNull();
            entry!.IsCrypted.Should().BeTrue();
            entry.AESKeySize.Should().Be(256); // AES-256

            ReadEntry(zf, entry).Should().Be(Content);
        }

        [Test]
        public void Aes_EncryptedArchive_FailsWithWrongPassword()
        {
            _fs.CreateZipFromDirectory(_source, _zip, includeSubfolders: false, password: "hunter2", useAesEncryption: true);

            using var zf = new SharpZip.ZipFile(_zip) { Password = "wrong" };
            var entry = zf.GetEntry(EntryName);
            Action read = () => ReadEntry(zf, entry!);
            read.Should().Throw<Exception>();
        }

        [Test]
        public void ZipCrypto_EncryptedArchive_RoundTripsWithCorrectPassword()
        {
            _fs.CreateZipFromDirectory(_source, _zip, includeSubfolders: false, password: "hunter2", useAesEncryption: false);

            using var zf = new SharpZip.ZipFile(_zip) { Password = "hunter2" };
            var entry = zf.GetEntry(EntryName);
            entry.Should().NotBeNull();
            entry!.IsCrypted.Should().BeTrue();
            entry.AESKeySize.Should().Be(0); // legacy ZipCrypto, not AES

            ReadEntry(zf, entry).Should().Be(Content);
        }

        [Test]
        public void NoPassword_ProducesAPlainUnencryptedArchive()
        {
            _fs.CreateZipFromDirectory(_source, _zip, includeSubfolders: false, comment: "fp", password: null);

            using var archive = System.IO.Compression.ZipFile.OpenRead(_zip); // BCL reads the plain path
            archive.Comment.Should().Be("fp");
            var entry = archive.GetEntry(EntryName);
            entry.Should().NotBeNull();
            using var reader = new StreamReader(entry!.Open());
            reader.ReadToEnd().Should().Be(Content);
        }

        private static string ReadEntry(SharpZip.ZipFile zf, SharpZip.ZipEntry entry)
        {
            using var stream = zf.GetInputStream(entry);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
