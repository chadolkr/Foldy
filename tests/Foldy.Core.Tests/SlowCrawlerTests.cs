using Foldy.Core.Search;

namespace Foldy.Core.Tests;

public sealed class SlowCrawlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-crawl-" + Guid.NewGuid().ToString("N"));
    private readonly string _store = Path.Combine(Path.GetTempPath(), "foldy-crawlstore-" + Guid.NewGuid().ToString("N"));

    public SlowCrawlerTests()
    {
        Directory.CreateDirectory(_root);
        SlowCrawler.MinPause = TimeSpan.Zero;
    }

    public void Dispose()
    {
        SlowCrawler.MinPause = TimeSpan.FromMilliseconds(25);
        foreach (var d in new[] { _root, _store })
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private void Make(string rel)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    [Fact]
    public void Crawls_everything_and_finds_by_name()
    {
        Make(@"영업\2026 제안서.pptx");
        Make(@"영업\견적\제안서_A사.docx");
        Make(@"인사\휴가 신청.hwp");
        using var c = new SlowCrawler(_store, () => [_root]);
        var n = c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(6, n); // 파일 3 + 폴더 3 (영업, 견적, 인사)

        var store = c.StoreFor(_root);
        var hits = store.Search("제안", 100).Select(e => Path.GetFileName(e.Path)).OrderBy(x => x).ToList();
        Assert.Equal(["2026 제안서.pptx", "제안서_A사.docx"], hits);
        Assert.Single(store.Search("휴가 신청", 100)); // 띄어 쓴 낱말은 모두 들어 있어야
        Assert.Empty(store.Search("영업 휴가", 100));
        Assert.Contains(store.Search("견적", 100), e => e.IsFolder);
        Assert.True(CrawlStatus_Complete(c));
    }

    [Fact]
    public void ReadAll_gives_whole_list_with_live_changes_for_disk_analysis()
    {
        Make(@"영업\견적\a.docx");
        Make(@"인사\b.hwp");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var store = c.StoreFor(_root);
        Assert.Equal(5, store.ReadAll().Count()); // 폴더 3 + 파일 2

        // Windows가 알려 준 변화: 영업 폴더째 지움 → 그 안도 빠진다. 새 파일은 더한다
        store.NoteRemoved(Path.Combine(_root, "영업"));
        store.NoteAdded(new CrawlEntry(Path.Combine(_root, "인사", "c.xlsx"), false, 1234, DateTime.UtcNow));
        var names = store.ReadAll().Select(e => Path.GetFileName(e.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["b.hwp", "c.xlsx", "인사"], names);
    }

    private bool CrawlStatus_Complete(SlowCrawler c) => c.StatusOf(_root)?.Complete == true;

    [Fact]
    public void While_crawling_what_is_gathered_so_far_is_searchable()
    {
        // 다 모으기 전에도(색인 중) 지금까지 모은 것으로 찾는다: 모으던 파일(.part)도 함께 읽는다
        Make(@"a\첫번째.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        var store = c.StoreFor(_root);
        File.WriteAllText(store.FilePath + ".part", Path.Combine(_root, "a", "모으는중.txt") + "\t0\t1\t0\n");
        Assert.Single(store.Search("모으는중", 10));
    }

    [Fact]
    public void Change_notes_show_up_right_away_and_deleted_ones_disappear()
    {
        Make(@"a\지울파일.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var store = c.StoreFor(_root);
        store.NoteAdded(new CrawlEntry(Path.Combine(_root, "a", "새파일.txt"), false, 1, DateTime.UtcNow));
        store.NoteRemoved(Path.Combine(_root, "a", "지울파일.txt"));
        Assert.Single(store.Search("새파일", 10));
        Assert.Empty(store.Search("지울파일", 10));
    }

    [Fact]
    public void Rescan_reads_only_changed_folders()
    {
        for (var i = 0; i < 10; i++)
        {
            Make($@"d{i}\f.txt");
        }

        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(11, c.LastPass!.Listed); // 처음: 뿌리 + 폴더 10개 모두 읽음

        Thread.Sleep(50);
        Make(@"d3\새파일.txt"); // d3의 수정 시각만 바뀐다
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var p = c.LastPass!;
        Assert.True(p.Listed <= 2, $"바뀐 폴더만 다시 읽어야 한다 (읽은 폴더 {p.Listed})");
        Assert.True(p.Reused >= 9);
        Assert.Single(c.StoreFor(_root).Search("새파일", 10));
        Assert.Equal(10, c.StoreFor(_root).Search("f.txt", 100).Count());
    }

    [Fact]
    public void Hidden_folders_and_links_are_skipped()
    {
        Make(@"보통\a.txt");
        Make(@"숨김\b.txt");
        File.SetAttributes(Path.Combine(_root, "숨김"), FileAttributes.Hidden | FileAttributes.Directory);
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var store = c.StoreFor(_root);
        Assert.Single(store.Search("a.txt", 10));
        Assert.Empty(store.Search("b.txt", 10));
    }

    [Fact]
    public void Cancel_keeps_the_previous_list()
    {
        Make(@"x\old.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Make(@"x\new.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        c.CrawlOnce(_root, cts.Token);
        var store = c.StoreFor(_root);
        Assert.Single(store.Search("old", 10));
        Assert.Empty(store.Search("new", 10)); // 다 모으기 전에는 예전 목록 그대로
        Assert.False(File.Exists(store.FilePath + ".part"));
    }

    [Fact]
    public void Slow_folders_make_it_pause_longer()
    {
        // 폴더를 여는 데 걸린 시간의 몇 배를 쉰다: 쉬는 시간이 최소값보다 길어지는지 (시간 비례 원칙)
        for (var i = 0; i < 30; i++)
        {
            Make($@"d{i}\f.txt");
        }

        SlowCrawler.MinPause = TimeSpan.FromMilliseconds(10);
        using var c = new SlowCrawler(_store, () => [_root]);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(31 * 10), "폴더마다 최소 10ms는 쉬어야 한다");
    }

    [Fact]
    public void Overflowed_drive_is_rescanned_soon_even_right_after_a_crawl()
    {
        // 변경 알림이 넘치면(Error) 1시간을 기다리지 않고 곧 다시 모은다
        Make(@"a\처음.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Make(@"a\알림놓침.txt");
        c.MarkDirty(_root);
        Assert.True(c.IsDirty(_root));
        c.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!c.StoreFor(_root).Search("알림놓침", 10).Any() && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(50);
        }

        Assert.Single(c.StoreFor(_root).Search("알림놓침", 10));
        Assert.False(c.IsDirty(_root));
    }
}
