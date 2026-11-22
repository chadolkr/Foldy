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
        VersionHistory.LocalDriveOverride = null;
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

    [Fact]
    public void A_tampered_index_cannot_make_foldy_delete_or_move_files_outside_the_history_folder()
    {
        // 보안 리뷰 2026-11-22 V1: 같은 공유 드라이브를 쓰는 다른 사람이 index.jsonl을 고쳐 둔 경우
        var victim = Path.Combine(_root, "중요한 파일.docx");
        File.WriteAllText(victim, "지키기");
        var f = Path.Combine(_root, "a.txt");
        File.WriteAllText(f, "1");
        var history = VersionHistory.HistoryRoot(f);
        Directory.CreateDirectory(history);
        var forged = new SavedVersion(Path.GetFullPath(f), victim, DateTime.UtcNow.AddDays(-30), 5);
        File.WriteAllText(Path.Combine(history, "index.jsonl"), System.Text.Json.JsonSerializer.Serialize(forged) + "\n");

        VersionHistory.Prune(f);
        File.Exists(victim).Should().BeTrue("보관 폴더 밖의 파일은 지우지 않는다");
        VersionHistory.VersionsOf(f).Should().BeEmpty("보관 폴더 밖을 가리키는 기록은 보여 주지 않는다");
        VersionHistory.Restore(forged).Should().BeFalse();
        VersionHistory.ExtractBeside(forged).Should().BeNull();
        File.ReadAllText(f).Should().Be("1");
    }

    [Fact]
    public void Files_on_network_or_removable_drives_are_not_kept()
    {
        VersionHistory.LocalDriveOverride = _ => false;
        var f = Path.Combine(_root, "공유.txt");
        File.WriteAllText(f, "1");
        VersionHistory.Preserve(f).Should().BeNull("공유 폴더·USB에는 예전 내용을 남기지 않는다");
        File.Exists(f).Should().BeTrue();
    }
}
