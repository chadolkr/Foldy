using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Foldy.UiTests;

/// <summary>
/// Foldy가 꺼져 있어도 단축키로 무엇이든 찾기 (1.11.0, 사용자 2026-11-24 "Foldy가 실행 안되어 있어도 ctrl + space 키를 누르면 foldy 검색창이 뜨도록").
/// 단축키 도우미는 Foldy.exe --quick-search를 띄운다. 1.15.0부터 주 창도 함께 뜨고(사용자 2026-11-26 "본창도 같이 뜨게"),
/// 찾기 창은 끌어서 옮길 수 있고 옮긴 자리에 다시 뜬다("Ctrl + space 화면이 고정되어 있는데 옮길 수 있게").
/// </summary>
[Collection("ui")]
public sealed class QuickSearchOnlyTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "foldy-ui-qs-" + Guid.NewGuid().ToString("N"));
    private readonly UIA3Automation _automation = new();
    private readonly List<Process> _started = [];

    private Process Launch(params string[] args)
    {
        Assert.SkipWhen(FoldyApp.Exe is null, "Foldy.exe가 없습니다 (dotnet build Foldy.slnx -c Release 먼저)");
        Directory.CreateDirectory(_data);
        File.WriteAllText(Path.Combine(_data, "tour-shown.txt"), "always");
        var psi = new ProcessStartInfo(FoldyApp.Exe!) { UseShellExecute = false };
        psi.Environment["FOLDY_DATA_DIR"] = _data;
        psi.Environment["FOLDY_LANGUAGE"] = "ko";
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private Window? TopWindow(int pid, Func<string, bool> title) =>
        _automation.GetDesktop().FindAllChildren(_automation.ConditionFactory.ByProcessId(pid))
            .Select(e => e.AsWindow())
            .FirstOrDefault(w => title(w.Title) && !w.IsOffscreen);

    private Window? FindWindow(int pid) => TopWindow(pid, t => t == "Foldy 빠른 검색");

    /// <summary>주 창 제목은 "Foldy 1.11.0".</summary>
    private Window? MainWindow(int pid) => TopWindow(pid, t => t.StartsWith("Foldy ", StringComparison.Ordinal) && char.IsDigit(t[^1]));

    [Fact]
    public void Quick_search_start_shows_the_main_window_too_and_the_find_window_can_be_moved()
    {
        var foldy = Launch("--quick-search");
        var qs = Retry.WhileNull(() => FindWindow(foldy.Id), TimeSpan.FromSeconds(30), ignoreException: true).Result;
        qs.Should().NotBeNull("Foldy가 꺼져 있어도 찾기 창은 떠야 한다");
        var main = Retry.WhileNull(() => MainWindow(foldy.Id), TimeSpan.FromSeconds(15), ignoreException: true).Result;
        main.Should().NotBeNull("주 창도 함께 뜬다 (1.15.0)");

        // 찾기 창은 평소처럼 쓸 수 있다
        AutomationElement? answer = null;
        for (var attempt = 0; attempt < 3 && answer is null; attempt++)
        {
            try
            {
                qs!.FindFirstDescendant(_automation.ConditionFactory.ByControlType(ControlType.Edit))!.AsTextBox().Text = "12*3+4";
                answer = Retry.WhileNull(() => qs.FindFirstDescendant(_automation.ConditionFactory.ByName("12*3+4 = 40")), TimeSpan.FromSeconds(5), ignoreException: true).Result;
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or NullReferenceException)
            {
                Thread.Sleep(1000);
            }
        }

        answer.Should().NotBeNull();

        // 맨 위 손잡이를 끌면 찾기 창이 옮겨진다
        var before = qs!.BoundingRectangle;
        var grip = new System.Drawing.Point(before.Left + before.Width / 2, before.Top + 24); // 창 둘레 그림자 여백 16 + 손잡이 가운데
        Mouse.MoveTo(grip);
        Mouse.Down(MouseButton.Left);
        Mouse.MoveTo(new System.Drawing.Point(grip.X - 120, grip.Y + 90));
        Mouse.Up(MouseButton.Left);
        var moved = Retry.WhileFalse(() => Math.Abs(qs.BoundingRectangle.Top - before.Top - 90) <= 6, TimeSpan.FromSeconds(5), ignoreException: true).Result;
        moved.Should().BeTrue($"손잡이를 끌면 창이 따라 옮겨져야 한다 (전 {before}, 뒤 {qs.BoundingRectangle})");
        var place = qs.BoundingRectangle;
        FindWindow(foldy.Id).Should().NotBeNull("옮기는 동안 찾기 창이 닫히면 안 된다");

        // 찾기 창을 닫아도 주 창은 그대로 남는다
        qs.Close();
        Thread.Sleep(1500);
        foldy.HasExited.Should().BeFalse();
        MainWindow(foldy.Id).Should().NotBeNull();

        // 다시 Foldy를 켜면: 새 Foldy는 앞의 Foldy에 넘기고 끝난다 (단일 인스턴스, 사용자 이름 해시 고침)
        var second = Launch();
        second.WaitForExit(15_000).Should().BeTrue("이미 켜진 Foldy가 있으면 넘기고 끝나야 한다");

        // 주 창이 떠 있는 Foldy에 --quick-search가 오면(도우미가 보냄) 옮긴 자리에 찾기 창이 뜬다
        var third = Launch("--quick-search");
        third.WaitForExit(15_000).Should().BeTrue();
        var again = Retry.WhileNull(() => FindWindow(foldy.Id), TimeSpan.FromSeconds(15), ignoreException: true).Result;
        again.Should().NotBeNull();
        var at = again!.BoundingRectangle;
        (Math.Abs(at.Left - place.Left) <= 2 && Math.Abs(at.Top - place.Top) <= 2).Should().BeTrue($"옮긴 자리에 다시 떠야 한다 (옮긴 자리 {place}, 다시 뜬 자리 {at})");
        File.Exists(Path.Combine(_data, "quick-search-place.txt")).Should().BeTrue();
        again.Close();
        // 주 창 닫기: 제목 표시줄 단추 대신 창 닫기 요청(WM_CLOSE)으로. 찾기 창이 막 닫히는 동안 단추가 잠깐 꺼져 있을 수 있다
        Retry.WhileException(() => main!.Patterns.Window.Pattern.Close(), TimeSpan.FromSeconds(10));
        // 프로세스가 끝나는 시간은 보지 않는다: CI에서는 다른 화면 시험도 닫은 뒤 바로 끝나지 않는다 (FoldyApp의 "Application failed to exit")
        Retry.WhileTrue(() => MainWindow(foldy.Id) is not null, TimeSpan.FromSeconds(15), ignoreException: true);
        MainWindow(foldy.Id).Should().BeNull("주 창이 닫혀야 한다. Foldy 기록 끝부분:\n" + LogTail());
    }

    private string LogTail() => FoldyApp.LogTail(_data);

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            p.Dispose();
        }

        _automation.Dispose();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(_data, recursive: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
