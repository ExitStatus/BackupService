using BackupService.FileSystem.Mtp;
using FluentAssertions;

namespace BackupService.UnitTests.FileSystem
{
    [TestFixture]
    public class ExifFilesTests
    {
        [TestCase(@"\DCIM\100MSDCF\DSC01234.ARW")]
        [TestCase(@"\DCIM\100APPLE\IMG_0001.HEIC")]
        [TestCase(@"\DCIM\Camera\photo.jpg")]
        public void PhotosAndRaws_MayHoldExif(string path)
        {
            ExifFiles.MayHoldExif(path).Should().BeTrue();
        }

        [TestCase(@"\DCIM\100MSDCF\C0001.MP4")]
        [TestCase(@"\DCIM\100APPLE\IMG_0002.MOV")]
        [TestCase(@"\PRIVATE\M4ROOT\CLIP\C0001.XML")]
        [TestCase(@"\DCIM\folder.jpg\noextension")]
        public void VideosAndOtherFiles_DoNot_SoTheyAreNeverDownloadedJustToLookForADate(string path)
        {
            ExifFiles.MayHoldExif(path).Should().BeFalse();
        }
    }
}
