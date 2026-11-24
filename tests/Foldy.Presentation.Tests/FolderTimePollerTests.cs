using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Tests;

public sealed class FolderTimePollerTests
{
    private static void WaitFor(Func<bool> ok)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ok() && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(20);
        }

        Assert.True(ok(), "10초 안에 되어야 한다");
    }

    [Fact]
    public void Refreshes_only_when_the_folder_time_changes_and_only_while_seen()
    {
        // 네트워크 드라이브에서 다른 프로그램이 파일을 저장하면 보고 있는 폴더가 곧 새로 고쳐진다 (사용자 2026-11-24)
        long time = 100;
        var asked = 0;
        var changed = 0;
        var seen = true;
        using var p = new FolderTimePoller(@"K:\work", () => Volatile.Read(ref seen), () => Interlocked.Increment(ref changed), _ =>
        {
            Interlocked.Increment(ref asked);
            return Interlocked.Read(ref time);
        });

        WaitFor(() => Volatile.Read(ref asked) >= 3);
        Assert.Equal(0, Volatile.Read(ref changed)); // 처음 본 시각은 기준일 뿐

        Interlocked.Exchange(ref time, 200);
        WaitFor(() => Volatile.Read(ref changed) == 1);

        Volatile.Write(ref seen, false); // 보이지 않는 탭은 묻지 않는다
        Thread.Sleep(1500);
        var before = Volatile.Read(ref asked);
        Thread.Sleep(2500);
        Assert.Equal(before, Volatile.Read(ref asked));
    }
}
