using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace Foldy.UiTests;

/// <summary>
/// 사용자 2026-11-25 "메세지가 커서 action 위치와 달라 볼 수 없다", "마우스 커서 아래에 보이게":
/// 두 창이 같은 폴더일 때 파일을 반대쪽 창으로 끌면, 놓을 수 없는 이유가 아래 상태 칸이 아니라 커서 바로 아래에 보인다.
/// </summary>
[Collection("ui")]
public sealed class HandHintTests
{
    /// <summary>
    /// 마우스로 누른 단추가 안 될 때(파일을 고르지 않고 [F5 복사]) 이유가 아래 상태 칸이 아니라 마우스 포인터 바로 옆에 보인다.
    /// 끌어 놓기도 같은 자리(CursorHint)를 쓴다. 시험 PC에서는 프로그램으로 흉내 낸 끌기가 Windows 끌기로 이어지지 않아,
    /// 끌어 놓기 규칙은 DropRulesTests로, 자리는 이 시험으로 확인한다.
    /// </summary>
    [Fact]
    public void Why_a_clicked_button_did_nothing_shows_right_next_to_the_pointer()
    {
        var empty = Path.Combine(Path.GetTempPath(), "foldy-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty); // 빈 폴더: 고른 파일이 없다
        try
        {
            using var app = FoldyApp.Start(openPath: empty);
            app.Main.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
            var copy = Retry.WhileNull(
                () => app.Main.FindAllDescendants(app.By.ByControlType(ControlType.Button)).FirstOrDefault(b => b.Name.StartsWith("F5", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(15), ignoreException: true).Result;
            copy.Should().NotBeNull("[F5 복사] 단추가 있다");
            var point = copy!.GetClickablePoint();
            Mouse.Click(point);

            var hint = Retry.WhileNull(() => FindHint(app), TimeSpan.FromSeconds(10), ignoreException: true).Result;
            hint.Should().NotBeNull("안 되는 이유가 누른 자리에 보여야 한다. 진단: " + Describe(app));
            hint!.Name.Should().Be("먼저 파일을 골라 주세요");

            // 포인터 바로 옆(아래, 화면 아래쪽이면 위)이고 포인터를 가리지 않는다
            var box = hint.BoundingRectangle;
            box.Contains(point).Should().BeFalse("포인터를 가리지 않게");
            Math.Min(Math.Abs(box.Top - point.Y), Math.Abs(box.Bottom - point.Y)).Should().BeLessThan(60, "포인터 바로 옆");
            Math.Abs(box.Left - point.X).Should().BeLessThan(120, "포인터 옆");

            Mouse.Click(new Point(point.X, point.Y - 200)); // 다른 곳을 누르면 사라진다
            Retry.WhileFalse(() => FindHint(app) is null, TimeSpan.FromSeconds(5), ignoreException: true).Result.Should().BeTrue("다른 곳을 누르면 안내가 사라져야 한다");
        }
        finally
        {
            try
            {
                Directory.Delete(empty, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 이름을 바꾸다 안 되면(쓸 수 없는 글자) 이유가 아래 상태 칸이 아니라 대상(이름을 바꾸던 줄) 바로 아래에 보인다 (키보드로 했을 때).
    /// </summary>
    [Fact]
    public void Why_a_rename_failed_shows_right_under_the_row()
    {
        using var app = FoldyApp.Start();
        var list = Retry.WhileNull(() => app.Main.FindAllDescendants(app.By.ByClassName("FastFileList")).OrderBy(l => l.BoundingRectangle.Left).FirstOrDefault(),
            TimeSpan.FromSeconds(15), ignoreException: true).Result;
        list.Should().NotBeNull();
        var row = ClickFirstRow(list!);
        row.Should().NotBeNull("메모.txt 줄을 찾아야 한다");

        Keyboard.Press(VirtualKeyShort.F2);
        Thread.Sleep(300);
        Keyboard.Type("a/b");
        Keyboard.Press(VirtualKeyShort.ENTER);

        var hint = Retry.WhileNull(() => FindHint(app), TimeSpan.FromSeconds(10), ignoreException: true).Result;
        hint.Should().NotBeNull("안 되는 이유가 이름을 바꾸던 자리에 보여야 한다");
        hint!.Name.Should().Contain("이름에는", "쓸 수 없는 글자 안내");
        var box = hint.BoundingRectangle;
        box.Top.Should().BeGreaterThan(row!.Value.Y, "그 줄 아래에");
        (box.Top - row.Value.Y).Should().BeLessThan(80, "그 줄 바로 아래");
        File.Exists(Path.Combine(app.Sample, "메모.txt")).Should().BeTrue("이름은 그대로");

        Keyboard.Press(VirtualKeyShort.DOWN); // 다음에 키를 누르면 사라진다
        Retry.WhileFalse(() => FindHint(app) is null, TimeSpan.FromSeconds(5), ignoreException: true).Result.Should().BeTrue("다음 키를 누르면 안내가 사라져야 한다");
    }

    /// <summary>목록의 첫 줄(메모.txt)을 눌러 고른다: 머리글 아래를 눌러 보며 목록이 알리는 이름으로 확인 (화면 배율과 상관없이). 누른 자리를 돌려준다.</summary>
    private static Point? ClickFirstRow(AutomationElement list)
    {
        for (var y = 20; y <= 90; y += 7) // 7px씩: 두 번 누르기로 묶이는 범위(4px)보다 멀리
        {
            var p = new Point(list.BoundingRectangle.Left + 60, list.BoundingRectangle.Top + y);
            Mouse.Click(p);
            Wait.UntilInputIsProcessed();
            if (Retry.WhileFalse(() => list.Name.Contains("메모.txt", StringComparison.Ordinal), TimeSpan.FromMilliseconds(400), ignoreException: true).Result)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>시험이 실패했을 때 무엇이 보였는지 (Foldy의 창들과 그 안 글 몇 개).</summary>
    private static string Describe(FoldyApp app)
    {
        try
        {
            var windows = app.Automation.GetDesktop().FindAllChildren(app.By.ByProcessId(app.App.ProcessId));
            return string.Join(" | ", windows.Select(w =>
                $"[{w.ControlType} '{w.Name}' {w.ClassName} {w.BoundingRectangle}] " +
                string.Join(", ", w.FindAllDescendants(app.By.ByControlType(FlaUI.Core.Definitions.ControlType.Text)).Take(6).Select(t => t.Name))))
                + $" · 마우스 {Mouse.Position}";
        }
        catch (Exception ex)
        {
            return "진단 실패: " + ex.Message;
        }
    }
}
