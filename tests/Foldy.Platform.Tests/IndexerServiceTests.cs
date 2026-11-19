using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Tests;

/// <summary>SYSTEM 서비스로 등록해도 되는 exe 위치 (사용자 폴더의 exe는 바꿔치기될 수 있다).</summary>
public sealed class IndexerServiceTests
{
    private static readonly string Pf = Path.Combine(Path.GetTempPath(), "foldy-pf-test", "Program Files");

    [Fact]
    public void Exe_under_program_files_is_protected()
    {
        IndexerService.IsProtectedLocation(Path.Combine(Pf, "Foldy", "foldy-index.exe"), [Pf]).Should().BeTrue();
    }

    [Fact]
    public void Exe_in_user_folders_or_escaping_is_not_protected()
    {
        var downloads = Path.Combine(Path.GetTempPath(), "foldy-pf-test", "Downloads", "Foldy", "foldy-index.exe");
        IndexerService.IsProtectedLocation(downloads, [Pf]).Should().BeFalse("압축을 푼 다운로드 폴더");
        IndexerService.IsProtectedLocation(Path.Combine(Pf, "..", "Downloads", "foldy-index.exe"), [Pf]).Should().BeFalse("..으로 빠져나감");
        IndexerService.IsProtectedLocation(Pf + " Evil" + Path.DirectorySeparatorChar + "foldy-index.exe", [Pf]).Should().BeFalse("이름만 비슷한 폴더");
        IndexerService.IsProtectedLocation(Path.Combine(Pf, "foldy-index.exe"), [null, ""]).Should().BeFalse();
    }
}
