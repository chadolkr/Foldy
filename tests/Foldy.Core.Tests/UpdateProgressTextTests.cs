namespace Foldy.Core.Tests;

// 업데이트 진행 창의 문구 규칙은 Foldy.App에 있어 여기서는 서비스 기록 문구가 바뀌지 않았는지만 지킨다.
public sealed class UpdateProgressTextTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    [Theory]
    [InlineData("Foldy가 끝나지 않아 설치를 미룸")]
    [InlineData("설치 시작: ")]
    [InlineData("설치 완료")]
    [InlineData("다시 띄움")]
    [InlineData("설치 실패: ")]
    public void Updater_log_still_writes_the_lines_the_progress_window_reads(string text)
    {
        var src = string.Join("\n", Directory.EnumerateFiles(Path.Combine(Root(), "engine", "foldy-index", "src", "update"), "*.rs").Select(File.ReadAllText));
        Assert.Contains(text, src, StringComparison.Ordinal);
    }
}
