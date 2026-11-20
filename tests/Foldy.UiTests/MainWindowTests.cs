using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Foldy.UiTests;

// UI 테스트는 화면 하나를 같이 쓰므로 한 번에 하나씩
[CollectionDefinition("ui", DisableParallelization = true)]
public sealed class UiCollection;

[Collection("ui")]
public sealed class MainWindowTests
{
    [Fact]
    public void Sidebar_shows_the_four_groups_and_three_bottom_icons()
    {
        using var app = FoldyApp.Start();
        foreach (var name in new[] { "찾기", "정리하기", "지키기", "연결하기", "휴지통", "설정", "도움말", "더 보기" })
        {
            app.Find(name).Should().NotBeNull(name);
        }

        // 0.3.20부터 업데이트·버그 신고·모든 명령 아이콘은 "더 보기" 안으로 옮겼다
        app.Main.FindFirstDescendant(app.By.ByName("버그 신고")).Should().BeNull();
    }

    [Fact]
    public void Group_button_opens_its_menu()
    {
        using var app = FoldyApp.Start();
        app.Find("정리하기").AsButton().Invoke();
        var item = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("바탕화면 정리").And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10)).Result;
        item.Should().NotBeNull("정리하기 메뉴에 바탕화면 정리가 있어야 한다");
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    [Fact]
    public void Small_laptop_screen_keeps_every_sidebar_tool_visible()
    {
        using var app = FoldyApp.Start();
        app.Main.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        app.Main.Patterns.Transform.Pattern.Resize(1366, 728);
        Thread.Sleep(1500);
        foreach (var name in new[] { "찾기", "정리하기", "지키기", "연결하기", "휴지통" })
        {
            var e = app.Find(name);
            e.IsOffscreen.Should().BeFalse($"1366×768에서 '{name}'이 스크롤 밖으로 밀리면 안 된다");
        }
    }

    [Fact]
    public void First_run_tour_shows_one_step_at_a_time_with_a_single_next_button()
    {
        using var app = FoldyApp.Start(skipTour: false);

        // 처음 설치: 먼저 보기 편하게(글자 크기·밝기)를 묻는다
        var setup = app.FindWindow("Foldy 시작하기", 20);
        app.Find("크게", setup).AsButton().Invoke();
        app.Find("이대로 좋아요", setup).AsButton().Invoke();
        app.Find("Windows 따라가기", setup); // 두 번째 장: 밝기
        app.Find("이대로 좋아요", setup).AsButton().Invoke();

        var tour = app.FindWindow("Foldy 둘러보기", 20);
        app.Find("둘러보기", tour).AsButton().Invoke();
        for (var step = 1; step <= 4; step++)
        {
            var next = app.Find("다음", tour);
            Thread.Sleep(300);
            tour.FindAllDescendants(app.By.ByName("다음").And(app.By.ByControlType(ControlType.Button))).Length.Should().Be(1, $"{step}번째 장: [다음]은 하나만 (말풍선으로 쌓이면 안 된다)");
            next.AsButton().Invoke();
        }

        app.Find("시작하기", tour).AsButton().Invoke();
    }
}
