using Foldy.Core.FileSystem;

namespace Foldy.Core.Tests;

/// <summary>왼쪽에 늘 보이는 기본 폴더의 위치: Windows Known Folder로 묻는다 (OneDrive로 옮긴 경우도).</summary>
public sealed class KnownFoldersTests
{
    [Fact]
    public void Windows_tells_where_each_folder_is()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 전용");
        foreach (var k in KnownFolders.Shown)
        {
            var p = KnownFolders.PathOf(k);
            p.Should().NotBeNullOrEmpty(k.ToString());
            Path.IsPathFullyQualified(p!).Should().BeTrue(p);
        }

        // 바탕 화면은 .NET도 Known Folder로 묻는다: 같은 자리여야 한다
        KnownFolders.PathOf(KnownFolder.Desktop).Should().BeEquivalentTo(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    [Fact]
    public void Every_folder_has_its_own_id()
    {
        KnownFolders.Shown.Select(KnownFolders.Id).Should().OnlyHaveUniqueItems();
    }
}
