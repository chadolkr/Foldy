using Foldy.Analysis;

namespace Foldy.Analysis.Tests;

/// <summary>
/// 색인 서비스가 없는 드라이브(K: 같은 회사 보안 드라이브)는 앱이 모아 둔 목록으로 분석한다 — 드라이브를 다시 읽지 않는다
/// (사용자 2026-11-23: "K 드라이브 분석을 시켰더니 폴더 읽는 중으로 나온다. 색인은 완료되었다는데…").
/// </summary>
public class CrawledListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Old = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private static List<CrawledItem> Sample(string root) =>
    [
        // 순서가 뒤섞여 있어도(부모 줄보다 자식이 먼저) 트리가 맞게 만들어져야 한다
        new($@"{root}web\node_modules\pkg0\index.js", false, 8 << 20, Old),
        new($@"{root}web", true, 0, Old),
        new($@"{root}web\node_modules", true, 0, Old),
        new($@"{root}web\node_modules\pkg0", true, 0, Old),
        new($@"{root}web\node_modules\pkg1", true, 0, Old),
        new($@"{root}web\node_modules\pkg1\index.js", false, 8 << 20, Old),
        new($@"{root}web\package.json", false, 100, Old),
        new($@"{root}docs\보고서.pdf", false, 70_000, Old),
        new($@"{root}readme", false, 3, Old),
        new(@"Z:\other\skip.txt", false, 999, Old), // 다른 곳은 넣지 않는다
    ];

    [Fact]
    public void Builds_tree_from_list_in_any_order()
    {
        var input = CrawledListSnapshot.Build(@"K:\", Sample(@"K:\"), Old, 4096, null, Ct);
        input.RootPath.Should().Be(@"K:\");
        input.Volume.Should().Be("K:");
        input.Files.Should().HaveCount(5);
        input.Dirs.Select(d => d.Name).Should().BeEquivalentTo(new[] { @"K:\", "web", "node_modules", "pkg0", "pkg1", "docs" });
        var web = input.Dirs.FindIndex(d => d.Name == "web");
        input.Dirs[web].Mtime.Should().Be(Old.ToFileTimeUtc(), "먼저 만들어 둔 폴더에도 목록의 시각을 채운다");
        input.Dirs[input.Dirs.FindIndex(d => d.Name == "node_modules")].Parent.Should().Be(web);
        input.Files.Single(f => f.Name == "readme").Alloc.Should().Be(4096);
        input.StartedAt.Should().Be(Old.ToFileTimeUtc(), "분석한 때 = 목록을 다 모은 때");
    }

    [Fact]
    public void Subfolder_root_takes_only_what_is_under_it()
    {
        var input = CrawledListSnapshot.Build(@"K:\web", Sample(@"K:\"), Old, 32 * 1024, null, Ct);
        input.RootPath.Should().Be(@"K:\web");
        input.Files.Select(f => f.Name).Should().BeEquivalentTo(new[] { "index.js", "index.js", "package.json" });
        input.Files.Single(f => f.Name == "package.json").Alloc.Should().Be(32 * 1024, "FAT/exFAT처럼 큰 할당 단위");
    }

    [Fact]
    public async Task Coordinator_uses_crawled_list_and_does_not_read_the_drive()
    {
        using var outDir = new TempDir();
        string? asked = null;
        var coordinator = new AnalysisCoordinator(null, CleanupRules.Default, outDir.Path)
        {
            CrawledLists = drive =>
            {
                asked = drive;
                return new CrawledList(DateTime.UtcNow, _ => Sample(@"Q:\"));
            },
        };
        var progress = new List<AnalysisProgress>();

        // Q:는 시험 PC에 없는 드라이브다. 직접 읽으려 했다면 DirectoryNotFoundException이 났을 것이다.
        var set = await coordinator.AnalyzeAsync(@"Q:\", new SyncProgress(progress), Ct);
        asked.Should().Be(@"Q:\");
        set.FromList.Should().BeTrue("화면에 '모아 둔 목록'으로 보인다 (직접 열거가 아니라)");
        (await coordinator.FindLatestAsync(@"Q:\", Ct))!.FromList.Should().BeTrue("다시 열어도 같다");
        progress.Should().Contain(p => p.Message.Contains("모아 둔 목록", StringComparison.Ordinal));
        progress.Should().NotContain(p => p.Message.Contains("폴더 읽는 중", StringComparison.Ordinal));
        using var db = AnalysisDb.Open(set.Latest.DbPath);
        db.Scan().TotalFiles.Should().Be(5);
        db.Candidates().Select(c => c.Rule).Should().Contain("node-modules");
    }

    [Fact]
    public async Task Without_list_falls_back_to_reading_folders_with_counts()
    {
        using var tree = new TempDir();
        using var outDir = new TempDir();
        tree.File(@"a\one.bin", 10);
        var coordinator = new AnalysisCoordinator(null, CleanupRules.Default, outDir.Path) { CrawledLists = _ => null };
        var progress = new List<AnalysisProgress>();
        var set = await coordinator.AnalyzeAsync(tree.Path, new SyncProgress(progress), Ct);
        set.FromService.Should().BeFalse();
        set.FromList.Should().BeFalse();
        progress[0].Message.Should().Contain("직접 읽는 중");
    }

    [Fact]
    public void Reading_progress_says_counts_and_time_and_uses_last_total()
    {
        var p = FallbackScanner.ReadingProgress(12, 3400, 0, TimeSpan.FromSeconds(75));
        p.Message.Should().StartWith("폴더 읽는 중… 폴더 12개").And.EndWith("(1분 지남)");
        var q = FallbackScanner.ReadingProgress(12, 5_000, 10_000, TimeSpan.FromSeconds(9));
        q.Message.Should().Contain("지난번 기준 약 50%");
        q.Fraction.Should().BeApproximately(0.275, 0.001);
        FallbackScanner.ReadingProgress(1, 50_000, 10_000, TimeSpan.Zero).Fraction.Should().BeLessThan(0.5, "지난번보다 많아도 넘치지 않는다");
    }

    private sealed class SyncProgress(List<AnalysisProgress> sink) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => sink.Add(value);
    }
}
