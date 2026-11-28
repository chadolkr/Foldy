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
        SlowCrawler.MinPause = TimeSpan.FromMilliseconds(1);
        foreach (var d in new[] { _root, _store })
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 임시 폴더 치우기만 실패한 것 (정션을 지울 때 이 노트북의 Windows가 "권한 없음"을 낸 적 있음, 2026-11-28)
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
    public void Hidden_folders_are_gathered_but_windows_folders_are_not()
    {
        // 회사 클라우드 드라이브(K:)는 맨 위 폴더가 숨김이다: 숨김 폴더도 모은다 (사용자 2026-11-23 "K 드라이브는 폴더가 히든 이라서 그럴까?")
        Make(@"보통\a.txt");
        Make(@"숨김\b.txt");
        Make(@"$RECYCLE.BIN\c.txt");
        Make(@"보통\desktop.ini");
        File.SetAttributes(Path.Combine(_root, "숨김"), FileAttributes.Hidden | FileAttributes.Directory);
        File.SetAttributes(Path.Combine(_root, "보통", "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var store = c.StoreFor(_root);
        Assert.Single(store.Search("a.txt", 10));
        Assert.Single(store.Search("b.txt", 10));
        Assert.Empty(store.Search("c.txt", 10)); // 휴지통은 Windows 것
        Assert.Empty(store.Search("desktop.ini", 10)); // 숨김+시스템 파일
        File.SetAttributes(Path.Combine(_root, "보통", "desktop.ini"), FileAttributes.Normal);
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
        Assert.True(c.HasUnfinished(_root)); // 끊긴 곳을 남겨 다음에 이어서 한다
    }

    [Fact]
    public void Checking_again_says_a_complete_list_already_exists()
    {
        // 업데이트 뒤 다시 모을 때 "처음부터 다시 하는 것 같다"(사용자 2026-11-26): 다 만든 목록이 있으면 그 시각을 함께 알려
        // 화면이 "바뀐 곳 확인 중"으로 보이게 한다. 그동안 찾기는 예전 목록으로 그대로 된다.
        Make(@"a\one.txt");
        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var running = new List<CrawlStatus>();
        c.Changed += s => { if (s.Running) { lock (running) { running.Add(s); } } };
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.NotEmpty(running);
        Assert.All(running, s => Assert.NotNull(s.CompletedUtc));
        Assert.All(running, s => Assert.False(s.Resumed));
    }

    [Fact]
    public void Interrupted_first_gathering_continues_where_it_stopped()
    {
        // 크고 느린 드라이브를 처음 모으다 Foldy를 끄거나 업데이트해도 처음부터 다시 하지 않는다 (사용자 2026-11-23)
        for (var i = 0; i < 20; i++)
        {
            Make($@"d{i:00}\f{i:00}.txt");
        }

        using (var cts = new CancellationTokenSource())
        using (var first = new SlowCrawler(_store, () => [_root]))
        {
            var n = 0;
            first.ShouldPause = () =>
            {
                if (++n > 2) // 폴더 몇 개를 같이 읽으므로 몇 묶음만에 끊는다
                {
                    cts.Cancel();
                }

                return false;
            };
            first.CrawlOnce(_root, cts.Token);
            Assert.True(first.HasUnfinished(_root));
            Assert.False(first.StoreFor(_root).Exists, "다 모으기 전에는 완료로 보이지 않는다");
        }

        using var c = new SlowCrawler(_store, () => [_root]);
        var asked = 0;
        c.ShouldPause = () => { asked++; return false; };
        var running = new List<CrawlStatus>();
        c.Changed += s => { if (s.Running) { lock (running) { running.Add(s); } } };
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        var p = c.LastPass!;
        Assert.True(p.Resumed);
        Assert.NotEmpty(running);
        Assert.All(running, s => Assert.True(s.Resumed, "화면에 \"이어서 만드는 중\"으로 보이게 알린다 (사용자 2026-11-26)"));
        Assert.True(asked < 21, $"이어서 했으면 남은 폴더만 읽는다 (이번에 본 폴더 {asked})");
        Assert.Equal(21, p.Listed); // 앞에서 읽은 것까지 합쳐 뿌리 + 폴더 20개
        Assert.Equal(20, c.StoreFor(_root).Search(".txt", 100).Count());
        Assert.Equal(40, c.StoreFor(_root).ReadAll().Count());
        Assert.False(c.HasUnfinished(_root));
        Assert.False(File.Exists(c.StoreFor(_root).FilePath + ".part"));
        Assert.False(File.Exists(c.StoreFor(_root).FilePath + ".marks"));
    }

    [Fact]
    public void Favorite_folders_are_gathered_first()
    {
        // 자주 쓰는 폴더가 있는 곳부터 모은다: 다 모으기 전에도 쓰는 파일이 먼저 찾아진다 (사용자 2026-11-24)
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            Make($@"{name}\{name}파일.txt");
        }

        using var cts = new CancellationTokenSource();
        using var c = new SlowCrawler(_store, () => [_root]) { PreferFirst = () => [Path.Combine(_root, "c")] };
        var n = 0;
        c.ShouldPause = () =>
        {
            if (++n > 1)
            {
                cts.Cancel(); // 맨 위 폴더 다음 한 묶음만 읽고 끊는다
            }

            return false;
        };
        c.CrawlOnce(_root, cts.Token);
        var store = c.StoreFor(_root);
        Assert.Single(store.Search("c파일", 10));
        Assert.Empty(store.Search("a파일", 10));
    }

    [Fact]
    public void Drive_that_keeps_folder_times_while_contents_change_is_found_out_and_read_whole()
    {
        // 하위 폴더가 있는 폴더는 늘 다시 읽으므로 "시각은 그대로인데 안이 바뀐" 폴더를 스스로 센다
        for (var i = 0; i < 4; i++)
        {
            Make($@"p{i}\s\f.txt");
        }

        using var c = new SlowCrawler(_store, () => [_root]);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.True(c.LastPass!.TrustTimes);
        for (var i = 0; i < 4; i++)
        {
            var p = Path.Combine(_root, $"p{i}");
            var before = Directory.GetLastWriteTimeUtc(p);
            Make($@"p{i}\새{i}.txt");
            Directory.SetLastWriteTimeUtc(p, before);
        }

        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.False(c.LastPass!.TrustTimes);
        Assert.Equal(4, c.StoreFor(_root).Search("새", 10).Count());

        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(9, c.LastPass!.Listed); // 믿지 않으니 뿌리 + 폴더 8개 모두
    }

    [Fact]
    public void Regular_gathering_is_skipped_while_free_space_and_top_folder_stay_the_same()
    {
        Make(@"a\처음.txt");
        using (var first = new SlowCrawler(_store, () => [_root]) { FreeSpaceOf = _ => 123 })
        {
            first.CrawlOnce(_root, TestContext.Current.CancellationToken);
        }

        Make(@"a\나중.txt"); // 맨 위 폴더 시각은 그대로
        using (var quiet = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(1), FreeSpaceOf = _ => 123 })
        {
            File.SetLastWriteTimeUtc(quiet.StoreFor(_root).FilePath, DateTime.UtcNow.AddHours(-2));
            quiet.Start();
            WaitFor(() => quiet.StatusOf(_root) is { Running: false, Complete: true });
            Thread.Sleep(500);
            Assert.Null(quiet.LastPass);
        }

        // 남은 공간이 바뀌었다: 이번에는 모은다
        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(1), FreeSpaceOf = _ => 456 };
        c.Start();
        WaitFor(() => c.LastPass is not null && c.StoreFor(_root).Search("나중", 10).Any());
    }

    [Fact]
    public void Drive_with_files_that_reads_as_empty_is_a_problem_not_done()
    {
        // 2026-11-23 회사 PC: 29GB를 쓴 K:가 "색인 완료 · 0개"로 나왔다
        Make(@"a\예전.txt");
        using var c = new SlowCrawler(_store, () => [_root]) { IsWholeDrive = _ => true, UsedSpaceOf = _ => 29L * 1024 * 1024 * 1024 };
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Directory.Delete(Path.Combine(_root, "a"), recursive: true);
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(SlowCrawler.NothingReadable, c.StatusOf(_root)!.Error);
        Assert.Single(c.StoreFor(_root).Search("예전", 10)); // 예전 목록은 그대로
        Assert.False(c.HasUnfinished(_root));
    }

    [Fact]
    public void Far_drive_rests_at_least_the_minimum_per_folder()
    {
        // 먼 드라이브(네트워크·클라우드)는 폴더마다 최소 시간을 쉰다 (모아서 한 번에 쉬어도 합은 같다)
        for (var i = 0; i < 30; i++)
        {
            Make($@"d{i}\f.txt");
        }

        SlowCrawler.MinPause = TimeSpan.FromMilliseconds(10);
        using var c = new SlowCrawler(_store, () => [_root]) { IsNear = _ => false };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(30 * 10), "폴더마다 10ms씩은 쉬어야 한다");
    }

    [Fact]
    public void Network_drive_rests_longer_than_a_cloud_drive_because_others_share_the_server()
    {
        for (var i = 0; i < 40; i++)
        {
            Make($@"d{i}\f.txt");
        }

        TimeSpan Gather(bool network)
        {
            using var c = new SlowCrawler(Path.Combine(_store, network ? "n" : "c"), () => [_root]) { IsNear = _ => false, IsNetwork = _ => network };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            c.CrawlOnce(_root, TestContext.Current.CancellationToken);
            return sw.Elapsed;
        }

        SlowCrawler.MinPause = TimeSpan.Zero;
        Assert.True(Gather(network: true) >= SlowCrawler.MinNetworkPause * 35, "서버를 여럿이 쓰는 네트워크 드라이브는 폴더마다 쉬어야 한다");
        Assert.True(SlowCrawler.MinNetworkPause > TimeSpan.FromMilliseconds(1));
        using var d = new SlowCrawler(_store, () => [_root]);
        Assert.True(d.IsNetwork(@"\\server\share\"));
        Assert.False(d.IsNetwork(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void Near_drive_is_gathered_without_the_minimum_rest()
    {
        for (var i = 0; i < 60; i++)
        {
            Make($@"d{i}\f.txt");
        }

        SlowCrawler.MinPause = TimeSpan.FromMilliseconds(100);
        using var c = new SlowCrawler(_store, () => [_root]) { IsNear = _ => true };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = c.CrawlOnce(_root, TestContext.Current.CancellationToken);
        Assert.Equal(120, n);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"가까운 드라이브인데 {sw.Elapsed.TotalSeconds:F1}초 걸림 (폴더마다 쉬면 6초)");
    }

    [Fact]
    public void Cloud_drive_is_never_near_even_when_windows_calls_it_fixed()
    {
        using var c = new SlowCrawler(_store, () => [_root]) { AlwaysReadAll = _ => true };
        Assert.False(c.IsNear(Path.GetPathRoot(_root)!)); // Google Drive는 "고정 드라이브"로 보인다
        using var local = new SlowCrawler(_store, () => [_root]);
        Assert.True(local.IsNear(Path.GetPathRoot(_root)!));
        Assert.False(local.IsNear(@"\\server\share\"));
    }

    [Fact]
    public void Folder_time_comes_with_its_listing()
    {
        Make(@"a\1.txt");
        var dir = Path.Combine(_root, "a");
        File.WriteAllText(Path.Combine(dir, "2.txt"), "x");
        var entries = SlowCrawler.List(dir, out var time);
        Assert.Equal(Directory.GetLastWriteTimeUtc(dir).Ticks, time); // 폴더를 한 번 더 열지 않는다
        Assert.Equal(["1.txt", "2.txt"], entries.Select(e => Path.GetFileName(e.Path)).Order()); // "."과 ".."는 목록에 넣지 않는다
    }

    /// <summary>Windows 목록 읽기(WinDirList)는 .NET 폴더 읽기와 같은 목록을 준다: 크기·시각·폴더 여부, 숨김+시스템 파일 빼기, 긴 위치.</summary>
    [Fact]
    public void Windows_listing_matches_dotnet_listing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 전용");
        }

        Make(@"a\작은.txt");
        File.WriteAllText(Path.Combine(_root, "a", "큰.bin"), new string('x', 70_000));
        Directory.CreateDirectory(Path.Combine(_root, "a", "하위"));
        var hidden = Path.Combine(_root, "a", "desktop.ini");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        var dir = Path.Combine(_root, "a");

        var win = SlowCrawler.List(dir, out var winTime);
        var net = SlowCrawler.ListPortable(dir, out var netTime);
        Assert.Equal(netTime, winTime);
        Assert.Equal(
            net.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => (e.Path, e.IsFolder, e.Size, e.ModifiedUtc)),
            win.OrderBy(e => e.Path, StringComparer.Ordinal).Select(e => (e.Path, e.IsFolder, e.Size, e.ModifiedUtc)));
        Assert.Equal(["작은.txt", "큰.bin", "하위"], win.Select(e => Path.GetFileName(e.Path)).Order(StringComparer.Ordinal));

        // 260자보다 긴 위치
        var deep = _root;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, "아주긴폴더이름_" + new string('d', 20));
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "끝.txt"), "x");
        Assert.Single(SlowCrawler.List(deep, out _));
        Assert.Throws<DirectoryNotFoundException>(() => SlowCrawler.List(Path.Combine(_root, "없는폴더"), out _));
    }

    /// <summary>정션(링크 폴더)은 들어가지 않는다: 목록의 재분석 태그로 알고, 그 폴더를 따로 열지 않는다.</summary>
    [Fact]
    public void Junction_is_skipped_from_the_listing_tag()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows 전용");
        }

        Make(@"진짜\a.txt");
        var link = Path.Combine(_root, "링크");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(_root, "진짜")}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            p.WaitForExit();
        }

        Assert.True(Directory.Exists(link), "정션을 만들지 못함");
        var names = SlowCrawler.List(_root, out _).Select(e => Path.GetFileName(e.Path)).ToList();
        Assert.Contains("진짜", names);
        Assert.DoesNotContain("링크", names);
        Assert.True(WinDirList.IsLinkTag(0xA0000003) && WinDirList.IsLinkTag(0xA000000C));
        Assert.False(WinDirList.IsLinkTag(0x9000001A), "클라우드 파일 폴더는 진짜 폴더");
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

    /// <summary>창 없이 뜬 Foldy(--gather, 1.13.0)는 쉬고 지나간 차례가 생기면 다 모은 것으로 보고 끝난다.</summary>
    [Fact]
    public void Idle_pass_is_counted_only_after_the_drive_is_gathered()
    {
        Make(@"a\b\c.txt");
        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(1) };
        Assert.Equal(0, c.IdlePasses);
        c.Start();
        WaitFor(() => c.IdlePasses > 0);
        Assert.True(CrawlStatus_Complete(c), "쉬고 지나가기 전에 다 모았어야 한다");
        Assert.Single(c.StoreFor(_root).Search("c.txt", 10));
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
        Assert.Equal(1, c.LastPass!.Listed); // 맨 위 폴더만 읽는다

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

    /// <summary>
    /// 변경 알림이 넘치거나 끊겨도(Error) 주기보다 자주 다시 모으지 않는다 (2026-11-25 K: 다 만든 지 7분 만에 1시간 넘는 다시 모으기).
    /// 주기가 되면 "바뀐 것 없어 보임"으로 건너뛰지 않고 꼭 모으고, 까닭을 알린다.
    /// </summary>
    [Fact]
    public void Overflowed_drive_waits_for_the_interval_but_is_not_skipped_as_quiet()
    {
        Make(@"a\처음.txt");
        using (var first = new SlowCrawler(_store, () => [_root]) { FreeSpaceOf = _ => 123 })
        {
            first.CrawlOnce(_root, TestContext.Current.CancellationToken);
        }

        var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (var soon = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(1), FreeSpaceOf = _ => 123 })
        {
            soon.Starting += (_, why) => started.Enqueue(why);
            soon.MarkDirty(_root);
            soon.Start();
            WaitFor(() => soon.IdlePasses > 0);
            Assert.Empty(started); // 방금 다 모았다: 알림이 끊겼어도 주기까지 기다린다
            Assert.True(soon.IsDirty(_root));
        }

        Make(@"a\알림놓침.txt"); // 맨 위 폴더 시각·남은 공간은 그대로라 보통은 건너뛸 정기 색인
        using var c = new SlowCrawler(_store, () => [_root]) { RescanEvery = TimeSpan.FromHours(1), FreeSpaceOf = _ => 123 };
        c.Starting += (_, why) => started.Enqueue(why);
        File.SetLastWriteTimeUtc(c.StoreFor(_root).FilePath, DateTime.UtcNow.AddHours(-2));
        c.MarkDirty(_root);
        c.Start();
        WaitFor(() => c.StoreFor(_root).Search("알림놓침", 10).Any());
        Assert.False(c.IsDirty(_root));
        Assert.Contains("주기 (변경 알림이 끊겼음)", started);
    }
}
