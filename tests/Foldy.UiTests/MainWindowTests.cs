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
        string[] names = ["찾기", "정리하기", "지키기", "연결하기", "휴지통"];
        // 시험 PC가 바쁘면 다시 배치되는 데 시간이 걸린다: 최대 15초 기다린다
        Retry.WhileTrue(() => names.Any(n => app.Find(n).IsOffscreen), TimeSpan.FromSeconds(15));
        foreach (var name in names)
        {
            var e = app.Find(name);
            e.IsOffscreen.Should().BeFalse($"1366×768에서 '{name}'이 스크롤 밖으로 밀리면 안 된다");
        }
    }

    [Fact]
    public void Quick_search_opens_and_calculates()
    {
        using var app = FoldyApp.Start();
        // 찾기 메뉴의 "빠른 검색"으로 연다 (키보드 입력은 함께 도는 다른 테스트와 겹칠 수 있다. Ctrl+Space 키는 같은 명령).
        // 바깥을 누르면 닫히는 창이라, 시험 PC가 바빠서 포커스가 흔들리면 다시 연다.
        AutomationElement? answer = null;
        Window? qs = null;
        for (var attempt = 0; attempt < 3 && answer is null; attempt++)
        {
            try
            {
                app.Find("찾기").AsButton().Invoke();
                var item = Retry.WhileNull(
                    () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("빠른 검색 (파일·앱·설정·기능·계산)").And(app.By.ByControlType(ControlType.MenuItem))),
                    TimeSpan.FromSeconds(10)).Result;
                item.Should().NotBeNull("찾기 메뉴에 빠른 검색이 있어야 한다");
                item!.AsMenuItem().Invoke();
                qs = app.FindWindow("Foldy 빠른 검색", 15);
                Thread.Sleep(500);
                qs.FindFirstDescendant(app.By.ByControlType(ControlType.Edit))!.AsTextBox().Text = "12*3+4"; // 입력 칸에 바로 (키보드 포커스와 무관)
                answer = Retry.WhileNull(() => qs.FindFirstDescendant(app.By.ByName("12*3+4 = 40")), TimeSpan.FromSeconds(5)).Result;
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                Thread.Sleep(1000);
            }
        }

        answer.Should().NotBeNull("계산 결과가 가장 알맞은 것으로 바로 보여야 한다");
        qs!.Close();
    }

    /// <summary>
    /// 2026-11-20 사용자 발견: "이미지만 보기"가 켜진 줄 모르고 그림 없는 폴더를 보면 파일이 사라진 것처럼 보였다.
    /// 켜져 있으면 그 사실과 [모든 파일 보기]가 같은 자리에 보이고, 누르면 파일 목록이 돌아와야 한다.
    /// </summary>
    [Fact]
    public void Images_only_mode_says_so_and_turns_off_in_place()
    {
        using var app = FoldyApp.Start();
        app.Find("찾기").AsButton().Invoke();
        var item = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("이미지만 보기").And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10)).Result;
        item.Should().NotBeNull("찾기 메뉴에 이미지만 보기가 있어야 한다");
        item!.AsMenuItem().Invoke();

        var off = app.Find("모든 파일 보기");
        off.IsOffscreen.Should().BeFalse("이미지만 보기를 끄는 단추가 목록 바로 위에 보여야 한다");
        off.AsButton().Invoke();
        Retry.WhileTrue(() => app.Main.FindFirstDescendant(app.By.ByName("모든 파일 보기")) is { IsOffscreen: false }, TimeSpan.FromSeconds(5));
        var after = app.Main.FindFirstDescendant(app.By.ByName("모든 파일 보기"));
        (after is null || after.IsOffscreen).Should().BeTrue("끄면 안내 줄이 사라져야 한다");
        app.Main.FindFirstDescendant(app.By.ByClassName("FastFileList")).Should().NotBeNull("파일 목록이 돌아와야 한다");
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
