using System.Diagnostics;
using Foldy.Core.Threading;

namespace Foldy.Core.Tests;

public class DriveWorkQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_on_dedicated_non_pool_thread()
    {
        await using var q = new DriveWorkQueue(DriveKey.FromPath(@"C:\"));
        var (isPool, name) = await q.Enqueue(_ => (Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name), WorkPriority.ActiveTab, Ct);
        isPool.Should().BeFalse();
        name.Should().StartWith("DriveQueue C:");
    }

    [Fact]
    public async Task Higher_priority_runs_first()
    {
        await using var q = new DriveWorkQueue(DriveKey.FromPath(@"C:\"));
        using var gate = new ManualResetEventSlim();
        var order = new List<string>();
        var blocker = q.Enqueue(_ => gate.Wait(Ct), WorkPriority.ActiveTab, Ct);
        var low = q.Enqueue(_ => { lock (order) { order.Add("size"); } }, WorkPriority.SizeCalc, Ct);
        var icons = q.Enqueue(_ => { lock (order) { order.Add("icons"); } }, WorkPriority.Icons, Ct);
        var tab = q.Enqueue(_ => { lock (order) { order.Add("tab"); } }, WorkPriority.ActiveTab, Ct);
        gate.Set();
        await Task.WhenAll(blocker, low, icons, tab);
        order.Should().Equal("tab", "icons", "size");
    }

    [Fact]
    public async Task Canceled_before_start_never_runs()
    {
        await using var q = new DriveWorkQueue(DriveKey.FromPath(@"C:\"));
        using var gate = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var ran = false;
        var blocker = q.Enqueue(_ => gate.Wait(Ct), WorkPriority.ActiveTab, Ct);
        var victim = q.Enqueue(_ => ran = true, WorkPriority.ActiveTab, cts.Token);
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => victim).Should().ThrowAsync<OperationCanceledException>();
        gate.Set();
        await blocker;
        await q.Enqueue(_ => { }, WorkPriority.ActiveTab, Ct); // 큐가 비워질 때까지
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Stalled_call_is_abandoned_and_queue_keeps_working()
    {
        // 응답 없는 네트워크 경로를 흉내: 5초 블로킹. 워치독(200ms)이 스레드를 버리고 새로 만든다.
        await using var q = new DriveWorkQueue(DriveKey.FromPath(@"\\nas\share\x"), TimeSpan.FromMilliseconds(200));
        using var release = new ManualResetEventSlim();
        var health = new List<DriveHealth>();
        q.HealthChanged += (_, h) => { lock (health) { health.Add(h); } };

        var stuck = q.Enqueue(_ => release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None), WorkPriority.ActiveTab, Ct);
        await FluentActions.Awaiting(() => stuck).Should().ThrowAsync<DriveUnresponsiveException>();
        q.Health.Should().Be(DriveHealth.Unresponsive);

        var sw = Stopwatch.StartNew();
        var after = await q.Enqueue(_ => 42, WorkPriority.ActiveTab, Ct);
        after.Should().Be(42);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "새 워커 스레드가 즉시 처리해야 한다");
        q.Health.Should().Be(DriveHealth.Ok);
        release.Set();
    }

    [Fact]
    public async Task One_stuck_drive_does_not_block_another()
    {
        await using var reg = new DriveQueueRegistry(TimeSpan.FromSeconds(10));
        using var release = new ManualResetEventSlim();
        var stuck = reg.ForPath(@"\\offline\share").Enqueue(c => release.Wait(c), WorkPriority.ActiveTab, Ct);

        var sw = Stopwatch.StartNew();
        var r = await reg.ForPath(@"C:\Windows").Enqueue(_ => "ok", WorkPriority.ActiveTab, Ct);
        r.Should().Be("ok");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        release.Set();
        await stuck;
    }

    [Theory]
    [InlineData(@"C:\Users\a", "C:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"\\nas\Share\dir\file", @"\\NAS\SHARE")]
    [InlineData(@"\\?\C:\very\long", "C:")]
    [InlineData(@"\\?\UNC\nas\share\x", @"\\NAS\SHARE")]
    public void DriveKey_normalizes(string path, string expected) =>
        DriveKey.FromPath(path).Value.Should().Be(expected);

    [Fact]
    public void DriveKey_rejects_relative() =>
        FluentActions.Invoking(() => DriveKey.FromPath(@"relative\path")).Should().Throw<ArgumentException>();
}
