using System.Runtime.InteropServices;
using Foldy.Platform.Preview;

namespace Foldy.Preview.Tests;

/// <summary>COM 처리기를 흉내 내는 관리 코드 개체. 호출 기록과 실패 주입.</summary>
internal sealed class FakeHandler : IPreviewHandler, IInitializeWithFile
{
    public List<string> Calls { get; } = [];

    public Exception? FailOn { get; init; }

    public string? FailAt { get; init; }

    public TimeSpan DoPreviewDelay { get; init; }

    private void Maybe(string name)
    {
        Calls.Add(name);
        if (FailAt == name && FailOn is not null)
        {
            throw FailOn;
        }
    }

    public void Initialize(string pszFilePath, uint grfMode) => Maybe("Initialize");

    public void SetWindow(IntPtr hwnd, ref PreviewRect prc) => Maybe("SetWindow");

    public void SetRect(ref PreviewRect prc) => Maybe("SetRect");

    public void DoPreview()
    {
        if (DoPreviewDelay > TimeSpan.Zero)
        {
            Thread.Sleep(DoPreviewDelay);
        }

        Maybe("DoPreview");
    }

    public void Unload() => Maybe("Unload");

    public void SetFocus() => Maybe("SetFocus");

    public void QueryFocus(out IntPtr phwnd)
    {
        phwnd = IntPtr.Zero;
        Maybe("QueryFocus");
    }

    public int TranslateAccelerator(ref PreviewMsg pmsg) => 1;
}

public class HandlerIsolationTests
{
    private static readonly Guid Clsid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Successful_open_previews_and_unloads_on_dispose()
    {
        var bl = new PreviewHandlerBlacklist();
        var h = new FakeHandler();
        var s = PreviewHandlerSession.Start(h, Clsid, @"C:\x.txt", IntPtr.Zero, 100, 50, bl, Timeout);
        h.Calls.Should().Equal("Initialize", "SetWindow", "DoPreview");
        s.IsOpen.Should().BeTrue();
        s.Dispose();
        s.Dispose(); // 두 번째는 무시
        h.Calls.Should().Equal("Initialize", "SetWindow", "DoPreview", "Unload");
        s.IsOpen.Should().BeFalse();
    }

    [Theory]
    [InlineData("Initialize")]
    [InlineData("SetWindow")]
    [InlineData("DoPreview")]
    public void Com_failure_is_wrapped_and_handler_is_unloaded(string failAt)
    {
        var bl = new PreviewHandlerBlacklist();
        var h = new FakeHandler { FailAt = failAt, FailOn = new COMException("boom", unchecked((int)0x80004005)) };
        var act = () => PreviewHandlerSession.Start(h, Clsid, @"C:\x.txt", IntPtr.Zero, 100, 50, bl, Timeout);
        act.Should().Throw<PreviewHandlerException>().Which.Clsid.Should().Be(Clsid);
        // SetWindow까지 갔으면(DoPreview 실패 포함) Unload로 정리하고, 그 전 단계 실패면 부르지 않는다
        h.Calls.Contains("Unload").Should().Be(failAt == "DoPreview");
        bl.FailureCount(Clsid).Should().Be(1);
    }

    [Fact]
    public void Handler_is_blacklisted_after_repeated_failures_and_success_resets()
    {
        var bl = new PreviewHandlerBlacklist(threshold: 3);
        for (var i = 0; i < 2; i++)
        {
            var bad = new FakeHandler { FailAt = "DoPreview", FailOn = new COMException("x") };
            var act = () => PreviewHandlerSession.Start(bad, Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, Timeout);
            act.Should().Throw<PreviewHandlerException>().Which.NowBlocked.Should().BeFalse();
        }

        bl.IsBlocked(Clsid).Should().BeFalse();
        PreviewHandlerSession.Start(new FakeHandler(), Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, Timeout).Dispose();
        bl.FailureCount(Clsid).Should().Be(0); // 성공하면 연속 횟수가 지워진다

        for (var i = 0; i < 3; i++)
        {
            var bad = new FakeHandler { FailAt = "Initialize", FailOn = new InvalidCastException() };
            var act = () => PreviewHandlerSession.Start(bad, Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, Timeout);
            var ex = act.Should().Throw<PreviewHandlerException>().Which;
            ex.NowBlocked.Should().Be(i == 2);
        }

        bl.IsBlocked(Clsid).Should().BeTrue();
    }

    [Fact]
    public void Slow_handler_is_closed_and_counted_as_failure()
    {
        var bl = new PreviewHandlerBlacklist();
        var h = new FakeHandler { DoPreviewDelay = TimeSpan.FromMilliseconds(150) };
        var act = () => PreviewHandlerSession.Start(h, Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, TimeSpan.FromMilliseconds(50));
        act.Should().Throw<PreviewHandlerException>().WithMessage("*느립니다*");
        h.Calls.Should().Contain("Unload");
        bl.FailureCount(Clsid).Should().Be(1);
    }

    [Fact]
    public void Object_without_preview_interface_is_rejected()
    {
        var bl = new PreviewHandlerBlacklist();
        var act = () => PreviewHandlerSession.Start(new object(), Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, Timeout);
        act.Should().Throw<PreviewHandlerException>();
        bl.FailureCount(Clsid).Should().Be(1);
    }

    [Fact]
    public void SetRect_failure_does_not_throw()
    {
        var bl = new PreviewHandlerBlacklist();
        var h = new FakeHandler { FailAt = "SetRect", FailOn = new COMException("gone") };
        using var s = PreviewHandlerSession.Start(h, Clsid, @"C:\x.txt", IntPtr.Zero, 10, 10, bl, Timeout);
        s.SetRect(200, 100);
        bl.FailureCount(Clsid).Should().Be(1);
    }

    [Fact]
    public void Open_without_registered_handler_throws_cleanly()
    {
        var act = () => PreviewHandlerSession.Open(@"C:\x.zzzfoldy", IntPtr.Zero, 10, 10);
        act.Should().Throw<PreviewHandlerException>();
    }

    [Fact]
    public void Unknown_extension_has_no_shell_handler() =>
        ShellPreviewHandlers.IsAvailable(".zzzfoldy").Should().BeFalse();
}
