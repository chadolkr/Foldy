using System.Diagnostics;
using System.Runtime.InteropServices;
using Foldy.Platform.SystemInfo;

namespace Foldy.Interop.Tests;

/// <summary>
/// 단축키 도우미 foldy-hotkey.exe (1.11.0): Foldy가 꺼져 있어도 단축키로 무엇이든 찾기 창을 연다.
/// 진짜 도우미를 띄우고 진짜 키를 눌러, 켜져 있는 Foldy(여기서는 같은 파이프를 여는 시험 서버)에 "--quick-search"가 가는지 본다.
/// 실제 Ctrl+Space 대신 아무도 쓰지 않는 Ctrl+Alt+Shift+F24를 쓴다 (시험 PC의 입력기·다른 프로그램과 겹치지 않게).
/// </summary>
public sealed partial class QuickSearchKeyAgentTests : IDisposable
{
    private static readonly string AgentExe = Path.Combine(AppContext.BaseDirectory, QuickSearchKeyAgent.ExeName);
    private const uint TestMods = 0x1 | 0x2 | 0x4; // Alt + Ctrl + Shift
    private const uint VkF24 = 0x87;

    private readonly string _localAppData = Path.Combine(Path.GetTempPath(), "foldy-hotkey-test-" + Guid.NewGuid().ToString("N"));
    private Process? _agent;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Prepare()
    {
        NativeRequirement.Ensure(File.Exists(AgentExe), QuickSearchKeyAgent.ExeName);
        if (QuickSearchKeyAgent.IsRunning)
        {
            Assert.Skip("이 PC에서 진짜 단축키 도우미가 켜져 있음 (같은 세션에 하나만 뜬다)");
        }
    }

    /// <summary>LOCALAPPDATA를 시험 폴더로 바꿔 띄운다 (도우미는 %LOCALAPPDATA%\Foldy\quick-search-key.txt를 읽는다).</summary>
    private Process StartAgent(string config)
    {
        QuickSearchKeyAgent.WriteConfig(Path.Combine(_localAppData, "Foldy", QuickSearchKeyAgent.ConfigFileName), config);
        var psi = new ProcessStartInfo(AgentExe) { UseShellExecute = false };
        psi.Environment["LOCALAPPDATA"] = _localAppData;
        _agent = Process.Start(psi)!;
        return _agent;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (cond())
            {
                return true;
            }

            await Task.Delay(30, Ct);
        }

        return cond();
    }

    [Fact]
    public async Task Turned_off_helper_ends_at_once_without_holding_a_key()
    {
        Prepare();
        var p = StartAgent(QuickSearchKeyAgent.Off);
        (await WaitUntilAsync(() => p.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue("꺼 두면 로그인 때 떠도 바로 끝나야 메모리를 쓰지 않는다");
        p.ExitCode.Should().Be(0);
        QuickSearchKeyAgent.IsRunning.Should().BeFalse();
    }

    /// <summary>1.13.0: 단축키를 쓰지 않아도 주기 모으기가 켜져 있으면 남아 있다 (키는 쥐지 않는다).</summary>
    [Fact]
    public async Task Helper_stays_for_gathering_without_holding_a_key()
    {
        Prepare();
        QuickSearchKeyAgent.WriteConfig(Path.Combine(_localAppData, "Foldy", QuickSearchKeyAgent.GatherFileName), "1");
        var p = StartAgent(QuickSearchKeyAgent.Off);
        (await WaitUntilAsync(() => QuickSearchKeyAgent.IsRunning, TimeSpan.FromSeconds(5))).Should().BeTrue("주기 모으기를 하려고 남는다");
        QuickSearchKeyAgent.HoldsKey.Should().BeFalse("단축키는 꺼 두었다");
        p.HasExited.Should().BeFalse();

        await QuickSearchKeyAgent.StopAsync(TimeSpan.FromSeconds(5));
        (await WaitUntilAsync(() => p.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task Key_press_reaches_running_Foldy_and_stop_ends_the_helper()
    {
        Prepare();
        using var foldy = SingleInstance.Acquire(); // 켜져 있는 Foldy 흉내: 같은 이름의 파이프를 연다
        if (!foldy.IsFirst)
        {
            Assert.Skip("이 PC에서 Foldy가 켜져 있음");
        }

        var got = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        foldy.StartServer(args => got.TrySetResult(args));

        var p = StartAgent(QuickSearchKeyAgent.FormatKey(TestMods, VkF24));
        (await WaitUntilAsync(() => QuickSearchKeyAgent.IsRunning, TimeSpan.FromSeconds(5))).Should().BeTrue("키를 쥐면 도우미 창이 생긴다");
        QuickSearchKeyAgent.HoldsKey.Should().BeTrue("키를 쥔 도우미 창은 이름이 key");

        PressTestKey();
        var args = await got.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        args.Should().Equal("--quick-search");

        await QuickSearchKeyAgent.StopAsync(TimeSpan.FromSeconds(5));
        QuickSearchKeyAgent.IsRunning.Should().BeFalse();
        (await WaitUntilAsync(() => p.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue("Foldy가 끄라고 하면 끝난다");
    }

    [Fact]
    public async Task Only_one_helper_per_session()
    {
        Prepare();
        var first = StartAgent(QuickSearchKeyAgent.FormatKey(TestMods, VkF24));
        (await WaitUntilAsync(() => QuickSearchKeyAgent.IsRunning, TimeSpan.FromSeconds(5))).Should().BeTrue();

        var psi = new ProcessStartInfo(AgentExe) { UseShellExecute = false };
        psi.Environment["LOCALAPPDATA"] = _localAppData;
        using var second = Process.Start(psi)!;
        (await WaitUntilAsync(() => second.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue("로그인 때 Run 두 곳에서 떠도 하나만 남는다");
        first.HasExited.Should().BeFalse();
        await QuickSearchKeyAgent.StopAsync(TimeSpan.FromSeconds(5));
    }

    private static void PressTestKey()
    {
        const uint up = 0x2;
        byte[] mods = [0x11, 0x12, 0x10]; // Ctrl, Alt, Shift
        foreach (var m in mods)
        {
            keybd_event(m, 0, 0, 0);
        }

        keybd_event((byte)VkF24, 0, 0, 0);
        keybd_event((byte)VkF24, 0, up, 0);
        foreach (var m in Enumerable.Reverse(mods))
        {
            keybd_event(m, 0, up, 0);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    public void Dispose()
    {
        try
        {
            if (_agent is { HasExited: false })
            {
                _agent.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _agent?.Dispose();
        try
        {
            Directory.Delete(_localAppData, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
