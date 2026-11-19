using AwesomeAssertions;
using Foldy.Platform.Imaging;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>Google Drive for desktop(G:) 같은 클라우드 드라이브 판별과 내용 읽기 차단.</summary>
public sealed class CloudVolumesTests
{
    [Theory]
    [InlineData("Google Drive", "FAT32", true)]
    [InlineData("google drive", null, true)]
    [InlineData(null, "GoogleDriveFS", true)]
    [InlineData("Local Disk", "NTFS", false)]
    [InlineData(null, "NTFS", false)]
    [InlineData(null, null, false)]
    public void Detects_cloud_volume_by_label_or_file_system(string? label, string? fs, bool expected) =>
        CloudVolumes.IsCloud(label, fs).Should().Be(expected);

    [Fact]
    public void Remembered_cloud_drive_blocks_content_reads_for_every_path_on_it()
    {
        CloudVolumes.Remember(@"Q:\", "Google Drive", "FAT32").Should().BeTrue();

        CloudVolumes.IsCloudPath(@"q:\내 드라이브\사진.jpg").Should().BeTrue();
        CloudFileGuard.MustNotReadContent(@"Q:\내 드라이브\사진.jpg").Should().BeTrue();

        CloudVolumes.Remember(@"Q:\", "Data", "NTFS").Should().BeFalse();
        CloudVolumes.IsCloudPath(@"Q:\a.txt").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData("sftp://host/a.txt")]
    public void Non_drive_paths_are_not_cloud_volumes(string? path) =>
        CloudVolumes.IsCloudPath(path).Should().BeFalse();
}
