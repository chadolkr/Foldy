using System.Drawing;
using System.Text.Json;
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
    [Fact]
    public void Dragging_onto_the_same_folder_shows_why_right_under_the_cursor()
    {
        using var app = FoldyApp.Start(prepare: a =>
        {
            // 두 창 모두 같은 샘플 폴더를 연 채로 시작한다
            var tab = new { kind = "folder", path = a.Sample };
            var pane = new { tabs = new[] { tab }, activeIndex = 0 };
            File.WriteAllText(Path.Combine(a.DataDir, "session.json"), JsonSerializer.Serialize(new { schemaVersion = 1, left = pane, right = pane, active = "left" }));
        });

        // 시험 PC 화면은 작아서 창 일부가 화면 밖에 있을 수 있다: 최대화해 두 목록이 모두 화면 안에 오게
        app.Main.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(500);

        AutomationElement[] Lists() => app.Main.FindAllDescendants(app.By.ByClassName("FastFileList")).OrderBy(l => l.BoundingRectangle.Left).ToArray();
        Retry.WhileFalse(() => Lists().Length == 2, TimeSpan.FromSeconds(15), ignoreException: true);
        var lists = Lists();
        lists.Should().HaveCount(2, "두 창에 파일 목록이 있다");
        var (left, right) = (lists[0], lists[1]);

        var row = ClickFirstRow(left);
        row.Should().NotBeNull("왼쪽 창에서 메모.txt 줄을 찾아야 한다");

        // 끌기 시작 → 오른쪽 창 가운데로
        var target = new Point(right.BoundingRectangle.Left + right.BoundingRectangle.Width / 2, right.BoundingRectangle.Top + right.BoundingRectangle.Height / 2);
        // 줄을 고른 클릭과 끌기 시작이 두 번 누르기(=파일 열기)로 합쳐지지 않게 두 번 누르기 시간보다 오래 기다린다
        Thread.Sleep(1000); // Windows 기본 두 번 누르기 시간은 500ms
        Mouse.MoveTo(row!.Value);
        Mouse.Down(MouseButton.Left);
        try
        {
            for (var i = 1; i <= 20; i++)
            {
                Mouse.MoveTo(new Point(row.Value.X + (target.X - row.Value.X) * i / 20, row.Value.Y + (target.Y - row.Value.Y) * i / 20));
                Thread.Sleep(30);
            }

            Mouse.MoveTo(new Point(target.X + 3, target.Y + 3)); // 놓을 곳 위에서 한 번 더 움직여 DragOver를 받게
            var hint = Retry.WhileNull(() => FindHint(app), TimeSpan.FromSeconds(10), ignoreException: true).Result;

            hint.Should().NotBeNull("놓을 수 없는 이유가 커서 아래에 보여야 한다. 진단: " + Describe(app));
            hint!.Name.Should().Be("이미 이 폴더에 있어요");

            // 커서 바로 아래: 커서보다 아래이고, 너무 멀지 않다
            var cursor = Mouse.Position;
            var box = hint.BoundingRectangle;
            box.Top.Should().BeGreaterThan(cursor.Y, "커서를 가리지 않게 아래에");
            (box.Top - cursor.Y).Should().BeLessThan(120, "커서 바로 아래");
            Math.Abs(box.Left - cursor.X).Should().BeLessThan(80, "커서 옆");
        }
        finally
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE); // 끌기 그만두기
            Mouse.Up(MouseButton.Left);
        }

        // 그만두면 안내가 사라진다
        Retry.WhileFalse(
            () => FindHint(app) is null,
            TimeSpan.FromSeconds(5), ignoreException: true).Result.Should().BeTrue("끌기를 그만두면 안내가 사라져야 한다");
        File.Exists(Path.Combine(app.Sample, "메모.txt")).Should().BeTrue("아무것도 옮기지 않는다");
    }

    /// <summary>커서 안내는 Foldy가 띄운 작은 팝업 창 안에 있다 (주 창 밖).</summary>
    private static AutomationElement? FindHint(FoldyApp app) =>
        app.Automation.GetDesktop().FindAllChildren(app.By.ByProcessId(app.App.ProcessId))
            .Select(w => w.FindFirstDescendant(app.By.ByAutomationId("CursorHint")))
            .FirstOrDefault(h => h is not null);

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
