using System.Runtime.InteropServices;
using Foldy.Core.Search;

namespace Foldy.Core.Tests;

/// <summary>드라이브를 모을 때의 빠르기: 가까운 드라이브는 쉬지 않고 남에게 비켜 주며, 먼 드라이브는 짧게 쉰다.</summary>
public sealed class CrawlPaceTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Near_drive_does_not_pause_while_it_answers_as_usual()
    {
        var pace = new CrawlPace(near: true, minPause: Ms(1));
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(TimeSpan.Zero, pace.After(Ms(0.2), 1, Ms(0.2)));
        }

        Assert.Equal(1, pace.Width); // 가까운 드라이브는 하나씩: CPU 한 줄까지만
    }

    [Fact]
    public void Near_drive_rests_when_answers_stay_slow_and_hurries_again_when_they_recover()
    {
        var pace = new CrawlPace(near: true, minPause: Ms(1));
        for (var i = 0; i < 50; i++)
        {
            pace.After(Ms(1), 1, Ms(1));
        }

        var rested = TimeSpan.Zero;
        for (var i = 0; i < 10; i++)
        {
            rested += pace.After(Ms(60), 1, Ms(60));
        }

        Assert.True(rested > Ms(30), $"느려졌는데 {rested.TotalMilliseconds}ms만 쉼");

        for (var i = 0; i < 200; i++)
        {
            pace.After(Ms(1), 1, Ms(1));
        }

        Assert.Equal(TimeSpan.Zero, pace.After(Ms(1), 1, Ms(1)));
    }

    [Fact]
    public void Short_pauses_add_up_to_one_real_pause()
    {
        // Windows는 1ms를 쉬어 달라고 해도 16ms쯤 쉰다: 모아 두었다가 한 번에 쉬어야 쉬는 시간이 부풀지 않는다
        var pace = new CrawlPace(near: false, minPause: Ms(1));
        var pauses = Enumerable.Range(0, 200).Select(_ => pace.After(Ms(0.1), 1, Ms(0.1))).ToList();
        Assert.All(pauses.Where(p => p > TimeSpan.Zero), p => Assert.True(p >= CrawlPace.Tick));
        var total = pauses.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        Assert.InRange(total, Ms(180), Ms(220)); // 폴더 200개에 1ms씩
    }

    [Fact]
    public void Oversleeping_is_paid_back()
    {
        var pace = new CrawlPace(near: false, minPause: Ms(1));
        var asked = TimeSpan.Zero;
        while (asked == TimeSpan.Zero)
        {
            asked = pace.After(Ms(0.1), 1, Ms(0.1));
        }

        pace.Slept(asked + Ms(30)); // 부탁보다 30ms 더 잤다
        for (var i = 0; i < 29; i++)
        {
            Assert.Equal(TimeSpan.Zero, pace.After(Ms(0.1), 1, Ms(0.1)));
        }
    }

    [Fact]
    public void Far_drive_rests_a_fifth_of_reading_time_and_reads_up_to_four_at_once()
    {
        var pace = new CrawlPace(near: false, minPause: Ms(1));
        var total = TimeSpan.Zero;
        for (var i = 0; i < 200; i++)
        {
            total += pace.After(Ms(100), pace.Width, Ms(100));
        }

        Assert.Equal(SlowCrawler.MaxWidth, pace.Width);
        Assert.InRange(total, Ms(200 * 100 * 0.18), Ms(200 * 100 * 0.3));
    }

    [Fact]
    public void Reading_gives_way_to_other_programs_and_returns_to_normal_after()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 전용");
        int before = -1, inside = -1, after = -1;
        var t = new Thread(() =>
        {
            before = IoPriority();
            GiveWay.Run(() => inside = IoPriority());
            after = IoPriority();
        });
        t.Start();
        t.Join();
        Assert.Equal(0, inside); // 안에서는 아주 낮음(0)
        Assert.True(before > 0, "양보하기 전부터 아주 낮으면 시험할 수 없다");
        Assert.Equal(before, after); // 끝나면 원래대로 (보통은 2, CI 실행기는 1)
    }

    [Fact]
    public void Giving_way_keeps_its_own_cpu_and_memory_turn()
    {
        // CPU까지 맨 뒤로 가면 PC가 바쁠 때 멈추다시피 한다 (2026-11-24: 폴더 60개에 10.9초). 디스크 차례만 양보한다
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 전용");
        var inside = 99;
        var memory = 99;
        var t = new Thread(() => GiveWay.Run(() =>
        {
            inside = GetThreadPriority(GetCurrentThread());
            Assert.Equal(0, NtQueryInformationThread(GetCurrentThread(), 24, out memory, 4, out _));
        }))
        { Priority = ThreadPriority.BelowNormal };
        t.Start();
        t.Join();
        Assert.Equal(-1, inside);
        Assert.Equal(5, memory); // 읽어 둔 것을 먼저 잊지 않게
    }

    [Fact]
    public void Giving_way_ends_even_when_reading_fails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows 전용");
        int before = -1, after = -1;
        var t = new Thread(() =>
        {
            before = IoPriority();
            try
            {
                GiveWay.Run(() => throw new IOException("끊김"));
            }
            catch (IOException)
            {
                after = IoPriority();
            }
        });
        t.Start();
        t.Join();
        Assert.True(before > 0);
        Assert.Equal(before, after);
    }

    private static int IoPriority()
    {
        Assert.Equal(0, NtQueryInformationThread(GetCurrentThread(), 22, out var v, 4, out _));
        return v;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(nint thread, int cls, out int value, int len, out int ret);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern int GetThreadPriority(nint thread);
}
