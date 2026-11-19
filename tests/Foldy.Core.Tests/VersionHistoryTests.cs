using Foldy.Core.History;

namespace Foldy.Core.Tests;

[Collection("VersionHistory")] // RootOverride는 전역 값이라 다른 테스트와 겹치지 않게
public sealed class VersionHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-hist-" + Guid.NewGuid().ToString("N"));

    public VersionHistoryTests()
    {
        Directory.CreateDirectory(_root);
        VersionHistory.RootOverride = _ => Path.Combine(_root, ".foldy", VersionHistory.HistoryName);
    }

    public void Dispose()
    {
        VersionHistory.RootOverride = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Preserve_moves_the_old_file_aside_and_restore_brings_it_back_keeping_the_newer_one()
    {
        var f = Path.Combine(_root, "보고서.docx");
        File.WriteAllText(f, "옛 내용");
        var v = VersionHistory.Preserve(f);
        v.Should().NotBeNull();
        File.Exists(f).Should().BeFalse("덮어쓰기 전에 옆으로 옮겨 둔다");
        File.ReadAllText(v!.Saved).Should().Be("옛 내용");

        File.WriteAllText(f, "새 내용"); // 엔진이 덮어쓴 것처럼
        VersionHistory.VersionsOf(f).Should().ContainSingle();
        VersionHistory.Restore(v).Should().BeTrue();
        File.ReadAllText(f).Should().Be("옛 내용");
        VersionHistory.VersionsOf(f).Should().ContainSingle("되돌리기 전 파일도 보관해서 다시 되돌릴 수 있다");

        var beside = VersionHistory.ExtractBeside(VersionHistory.VersionsOf(f)[0]);
        File.ReadAllText(beside!).Should().Be("새 내용");
        Path.GetFileName(beside).Should().StartWith("보고서 (이전 버전 ");
    }

    [Fact]
    public void Old_versions_are_pruned_after_seven_days()
    {
        var f = Path.Combine(_root, "a.txt");
        File.WriteAllText(f, "1");
        var old = VersionHistory.Preserve(f, DateTime.UtcNow.AddDays(-10));
        File.WriteAllText(f, "2");
        VersionHistory.Preserve(f);
        File.Exists(old!.Saved).Should().BeFalse("7일이 지난 보관본은 지운다");
        VersionHistory.VersionsOf(f).Should().ContainSingle();
    }

    [Fact]
    public void Missing_files_are_not_preserved()
    {
        VersionHistory.Preserve(Path.Combine(_root, "없음.txt")).Should().BeNull();
    }
}
