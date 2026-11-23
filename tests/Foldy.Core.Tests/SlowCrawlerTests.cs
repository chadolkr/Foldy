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

    private static void WaitFor(Func<bool> ok)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ok() && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(50);
        }

        Assert.True(ok(), "20초 안에 되어야 한다");
    }

    [Fact]
    public void Request_gathers_now_even_right_after_a_crawl_and_rereads_every_folder()
    {
        for (var i = 0; i < 10; i++)
        {
            Make($@"d{i}\f.txt");
        }

        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = null };
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        // 이름은 그대로, 크기만 바뀐 파일: 폴더 수정 시각이 안 바뀌어 보통 다시 모으기로는 알 수 없다
        File.WriteAllText(Path.Combine(_root, "d3", "f.txt"), new string('x', 5000));
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(0, c.LastPass!.Listed);

        Assert.True(c.RequestNow(_root));
        c.Start();
        WaitFor(() => c.LastPass!.Listed == 11);
        Assert.Contains(c.StoreFor(_root).Search("f.txt", 100), e => e.Size == 5000);
        Assert.False(c.IsRequested(_root));
    }

    [Fact]
    public void Drive_whose_folder_times_cannot_be_trusted_is_read_whole_every_time()
    {
        // Google Drive처럼: 파일이 생겼는데 폴더 수정 시각은 그대로
        Make(@"a\처음.txt");
        Make(@"b\다른.txt");
        var a = Path.Combine(_root, "a");
        var before = Directory.GetLastWriteTimeUtc(a);
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Make(@"a\나중.txt");
        Directory.SetLastWriteTimeUtc(a, before);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Empty(c.StoreFor(_root).Search("나중", 10)); // 시각만 보면 놓친다

        string? asked = null;
        c.AlwaysReadAll = d => { asked = d; return true; };
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Single(c.StoreFor(_root).Search("나중", 10));
        Assert.Equal(3, c.LastPass!.Listed); // 뿌리 + 폴더 2개 모두
        Assert.Equal(_root, asked);
    }

    [Fact]
    public void Requesting_twice_queues_once()
    {
        using var c = new SlowCrawler(_store, () => [_root]);
        Assert.True(c.RequestNow(_root));
        Assert.False(c.RequestNow(_root));
        Assert.True(c.IsRequested(_root));
    }

    [Fact]
    public void Request_for_a_drive_that_is_gone_is_dropped()
    {
        Make(@"a\f.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.RequestNow(Path.Combine(_root, "없는드라이브"));
        c.Start();
        WaitFor(() => !c.IsRequested(Path.Combine(_root, "없는드라이브")) && c.StoreFor(_root).Exists);
    }

    [Fact]
    public void Manual_only_never_gathers_by_itself_but_keeps_the_list()
    {
        Make(@"a\처음.txt");
        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = null };
        c.Start();
        WaitFor(() => c.StatusOf(_root) is { Running: false, Complete: false }); // 살펴봤지만 모으지 않았다
        Assert.False(c.StoreFor(_root).Exists);
        c.MarkDirty(_root);
        Thread.Sleep(500);
        Assert.False(c.StoreFor(_root).Exists);

        c.RequestNow(_root);
        WaitFor(() => c.StoreFor(_root).Search("처음", 10).Any());
    }

    [Fact]
    public void Regular_interval_counts_from_the_last_finished_time_across_restarts()
    {
        Make(@"a\처음.txt");
        using (var first = new SlowCrawler(_store, () => [_root]))
        {
            first.CrawlOnce(_root, TestContext.Current.CancellationToken);
        }

        Make(@"a\나중.txt");
        // 7시간 전에 다 모은 목록: 하루 주기면 그대로, 6시간 주기면 다시 모은다
        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(24) };
        File.SetLastWriteTimeUtc(c.StoreFor(_root).FilePath, DateTime.UtcNow.AddHours(-7));
        c.Start();
        WaitFor(() => c.StatusOf(_root) is { Running: false, Complete: true });
        Assert.Null(c.LastPass);

        c.RescanEvery = TimeSpan.FromHours(6); // 주기를 바꾸면 기다리지 않고 바로 본다
        WaitFor(() => c.LastPass is not null && c.StoreFor(_root).Search("나중", 10).Any());
        Assert.Contains(_root, c.KnownDrives);
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
