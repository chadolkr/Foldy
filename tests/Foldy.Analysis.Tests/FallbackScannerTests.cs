using Foldy.Analysis;

namespace Foldy.Analysis.Tests;

/// <summary>서비스 없는 드라이브용 폴백 스캐너: 임시 폴더 트리를 열거해 같은 스키마의 DB를 만든다.</summary>
public class FallbackScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scans_temp_tree_into_same_schema_with_candidates()
    {
        using var tree = new TempDir();
        using var outDir = new TempDir();
        var old = DateTime.UtcNow.AddDays(-90);
        tree.File(@"web\package.json", 100, old);
        for (var i = 0; i < 12; i++)
        {
            tree.File($@"web\node_modules\pkg{i}\index.js", 1 << 20, old);
        }

        tree.File(@"web\src\app.ts", 5000);
        tree.File(@"docs\보고서.PDF", 70_000);
        tree.File(@"docs\하위\notes.txt", 7);
        tree.File("readme", 3);

        var progress = new List<AnalysisProgress>();
        var (path, stats) = await FallbackScanner.ScanAsync(tree.Path, outDir.Path, CleanupRules.Default, new SyncProgress(progress), Ct);
        stats.Files.Should().Be(17);
        stats.Candidates.Should().Be(1);
        progress.Should().NotBeEmpty();
        progress[^1].Fraction.Should().Be(1);
        Path.GetFileName(path).Should().StartWith(AnalysisPaths.VolumeKey(tree.Path) + ".");

        using var db = AnalysisDb.Open(path);
        db.Scan().TotalFiles.Should().Be(17);
        db.PathOf(db.RootId).Should().Be(tree.Path);
        var nm = db.FindDir(Path.Combine(tree.Path, "web", "node_modules"));
        nm.Should().NotBeNull();
        var folder = db.Folder(nm!.Value)!;
        folder.Tag.Should().Be("node_modules");
        folder.TotalFiles.Should().Be(12);
        folder.TotalDirs.Should().Be(12);
        folder.Alloc.Should().Be(12L << 20);
        var c = db.Candidates().Single();
        c.Rule.Should().Be("node-modules");
        c.Path.Should().Be(Path.Combine(tree.Path, "web", "node_modules"));
        c.Reclaim.Should().Be(12L << 20);
        db.TopFiles(1)[0].Size.Should().Be(1 << 20);
        db.ExtensionStats().Single(e => e.Ext == "pdf").Files.Should().Be(1);
        db.ExtensionStats().Single(e => e.Ext == "").Files.Should().Be(1, "확장자 없는 파일");
        db.Children(db.RootId).Select(x => x.Name).Should().Contain(["web", "docs", "readme"]);
        AnalysisText.FromFileTime(db.Folder(nm.Value)!.NewestMtime).Should().BeCloseTo(old, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Second_scan_keeps_previous_for_growth_and_cancel_leaves_nothing()
    {
        using var tree = new TempDir();
        using var outDir = new TempDir();
        tree.File(@"a\one.bin", 10_000);
        var (first, _) = await FallbackScanner.ScanAsync(tree.Path, outDir.Path, CleanupRules.Default, null, Ct);
        tree.File(@"a\two.bin", 1 << 20);
        await Task.Delay(1100, Ct); // 파일 이름이 초 단위
        var (second, _) = await FallbackScanner.ScanAsync(tree.Path, outDir.Path, CleanupRules.Default, null, Ct);
        second.Should().NotBe(first);
        var snaps = AnalysisPaths.ListSnapshots(outDir.Path, AnalysisPaths.VolumeKey(tree.Path));
        snaps.Select(s => s.DbPath).Should().Equal(second, first);
        using (var db = AnalysisDb.Open(second))
        {
            var g = db.Growth(first, 10);
            g.Single(x => x.Name == "a").Delta.Should().Be(1 << 20);
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var before = Directory.GetFiles(outDir.Path).Length;
        await FluentActions.Awaiting(() => FallbackScanner.ScanAsync(tree.Path, outDir.Path, CleanupRules.Default, null, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(outDir.Path).Length.Should().Be(before, "취소하면 임시 파일도 남지 않는다");
    }

    [Fact]
    public void Missing_root_throws() =>
        FluentActions.Invoking(() => FallbackScanner.Enumerate(Path.Combine(Path.GetTempPath(), "foldy-none-" + Guid.NewGuid().ToString("N")), null, CancellationToken.None))
            .Should().Throw<DirectoryNotFoundException>();

    /// <summary>Progress&lt;T&gt;는 스레드 풀로 보내므로 시험에서는 바로 기록한다.</summary>
    private sealed class SyncProgress(List<AnalysisProgress> sink) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
