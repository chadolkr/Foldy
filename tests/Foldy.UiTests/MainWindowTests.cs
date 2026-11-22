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
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
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
        Retry.WhileTrue(() => names.Any(n => app.Find(n).IsOffscreen), TimeSpan.FromSeconds(15), ignoreException: true);
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
                    TimeSpan.FromSeconds(10), ignoreException: true).Result;
                item.Should().NotBeNull("찾기 메뉴에 빠른 검색이 있어야 한다");
                item!.AsMenuItem().Invoke();
                qs = app.FindWindow("Foldy 빠른 검색", 15);
                Thread.Sleep(500);
                qs.FindFirstDescendant(app.By.ByControlType(ControlType.Edit))!.AsTextBox().Text = "12*3+4"; // 입력 칸에 바로 (키보드 포커스와 무관)
                answer = Retry.WhileNull(() => qs.FindFirstDescendant(app.By.ByName("12*3+4 = 40")), TimeSpan.FromSeconds(5), ignoreException: true).Result;
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
    /// 2026-11-21 사용자: "양쪽 탐색기에 현재 폴더 위치가 표시되어야". 트리가 지금 폴더까지 펼쳐지고 그 폴더가 강조된다.
    /// </summary>
    [Fact]
    public void Folder_tree_follows_the_current_folder()
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FoldyApp.Exe ?? ".")!, "..", "..", "..", "..", "..", ".."));
        var target = Path.Combine(repo, "docs", "images"); // 숨김 폴더를 거치지 않는 곳
        using var app = FoldyApp.Start(openPath: target);
        AutomationElement? Selected() => app.Main.FindAllDescendants(app.By.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(t => t.Name == "images" && t.Patterns.SelectionItem.TryGetPattern(out var s) && s.IsSelected.Value);

        var hit = Retry.WhileNull(Selected, TimeSpan.FromSeconds(8), ignoreException: true).Result;
        // 좁은 화면(CI 등)에서 트리가 접혀 있으면 패널마다 트리 단추로 펴 본다 (펴는 순간 따라가야 한다)
        foreach (var toggle in hit is null ? app.Main.FindAllDescendants(app.By.ByName("폴더 트리 보이기/숨기기")) : [])
        {
            toggle.AsButton().Invoke();
            hit = Retry.WhileNull(Selected, TimeSpan.FromSeconds(8), ignoreException: true).Result;
            if (hit is not null)
            {
                break;
            }

            toggle.AsButton().Invoke(); // 이 패널이 아니면 되돌린다
        }

        hit.Should().NotBeNull("트리에서 지금 폴더(images)가 펼쳐져 강조돼야 한다: " + string.Join(", ",
            app.Main.FindAllDescendants(app.By.ByControlType(ControlType.TreeItem)).Select(t => t.Name + (t.Patterns.SelectionItem.TryGetPattern(out var s) && s.IsSelected.Value ? "*" : ""))));
        Retry.WhileTrue(() => hit!.IsOffscreen, TimeSpan.FromSeconds(5), ignoreException: true);
        hit!.IsOffscreen.Should().BeFalse("강조된 폴더가 보이게 스크롤돼야 한다");
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
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        item.Should().NotBeNull("찾기 메뉴에 이미지만 보기가 있어야 한다");
        item!.AsMenuItem().Invoke();

        var off = app.Find("모든 파일 보기");
        off.IsOffscreen.Should().BeFalse("이미지만 보기를 끄는 단추가 목록 바로 위에 보여야 한다");
        off.AsButton().Invoke();
        Retry.WhileTrue(() => app.Main.FindFirstDescendant(app.By.ByName("모든 파일 보기")) is { IsOffscreen: false }, TimeSpan.FromSeconds(5), ignoreException: true);
        var after = app.Main.FindFirstDescendant(app.By.ByName("모든 파일 보기"));
        (after is null || after.IsOffscreen).Should().BeTrue("끄면 안내 줄이 사라져야 한다");
        app.Main.FindFirstDescendant(app.By.ByClassName("FastFileList")).Should().NotBeNull("파일 목록이 돌아와야 한다");
    }

    [Fact]
    public void First_run_setup_then_short_video_intro_that_can_be_skipped()
    {
        using var app = FoldyApp.Start(skipTour: false);

        // 처음 설치: 먼저 보기 편하게(글자 크기·밝기)를 묻는다
        var setup = app.FindWindow("Foldy 시작하기", 20);
        app.Find("크게", setup).AsButton().Invoke();
        app.Find("이대로 좋아요", setup).AsButton().Invoke();
        app.Find("Windows 따라가기", setup); // 두 번째 장: 밝기
        app.Find("이대로 좋아요", setup).AsButton().Invoke();

        // 이어서 쇼츠처럼 보는 짧은 소개: 저절로 넘어가고 [건너뛰기]로 언제든 끝낸다
        var shorts = app.FindWindow("Foldy 짧은 소개", 20);
        app.Find("건너뛰기", shorts).AsButton().Invoke();
        Retry.WhileTrue(() => app.App.GetAllTopLevelWindows(app.Automation).Any(w => w.Title == "Foldy 짧은 소개"), TimeSpan.FromSeconds(5), ignoreException: true);
        app.App.GetAllTopLevelWindows(app.Automation).Any(w => w.Title == "Foldy 짧은 소개").Should().BeFalse("건너뛰기로 닫혀야 한다");
    }

    // ───────────── 기능 찾기 (2026-11-22, 사용자 "좋은 기능이 있어도 처음 사용하는 사람은 기능이 어디 있는지 모른다") ─────────────

    private static MenuItem? OpenGroupItem(FoldyApp app, string group, string item)
    {
        app.Find(group).AsButton().Invoke();
        var found = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName(item).And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        return found?.AsMenuItem();
    }

    /// <summary>더 보기 안에만 있던 AI 연결은 연결하기 묶음에서, 오른쪽 클릭에만 있던 이전 버전은 지키기 묶음에서도 찾힌다.</summary>
    [Fact]
    public void Hidden_features_are_in_the_group_where_people_look()
    {
        using var app = FoldyApp.Start();
        OpenGroupItem(app, "연결하기", "AI에 연결 (Claude 등)…").Should().NotBeNull("연결하기 메뉴 맨 위에 AI 연결이 있어야 한다");
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
        OpenGroupItem(app, "지키기", "덮어쓴 파일 되살리기 (이전 버전, 고른 파일)").Should().NotBeNull("지키기 메뉴에 이전 버전이 있어야 한다");
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    /// <summary>빈 폴더는 할 수 있는 일을 알려 주기 좋은 순간: 끌어 놓기·F7 안내와 [템플릿으로 폴더 만들기].</summary>
    [Fact]
    public void Empty_folder_says_what_you_can_do_and_offers_templates()
    {
        var empty = Path.Combine(Path.GetTempPath(), "foldy-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            using var app = FoldyApp.Start(openPath: empty);
            var button = Retry.WhileNull(() => app.Main.FindFirstDescendant(app.By.ByAutomationId("EmptyTemplateButton")), TimeSpan.FromSeconds(15), ignoreException: true).Result;
            button.Should().NotBeNull("빈 폴더에 [템플릿으로 폴더 만들기]가 보여야 한다");
            button!.IsOffscreen.Should().BeFalse();
            app.Main.FindFirstDescendant(app.By.ByAutomationId("EmptyTip")).Should().NotBeNull("끌어 놓기·F7 안내 한 줄");
            button.AsButton().Invoke();
            app.FindWindow("템플릿으로 만들기", 15).Should().NotBeNull("누르면 템플릿 고르기 창이 열린다");
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

    /// <summary>사진이 대부분인 폴더를 열면 "이미지만 보기"를 한 줄로 알려 주고, 누르면 바로 켜진다.</summary>
    [Fact]
    public void Photo_folder_offers_images_only_once_in_one_line()
    {
        var photos = Path.Combine(Path.GetTempPath(), "foldy-photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(photos);
        for (var i = 0; i < 35; i++)
        {
            File.WriteAllBytes(Path.Combine(photos, $"IMG_{i:000}.jpg"), [0xFF, 0xD8, 0xFF]);
        }

        try
        {
            using var app = FoldyApp.Start(openPath: photos);
            var button = Retry.WhileNull(
                () => app.Main.FindFirstDescendant(app.By.ByName("이미지만 보기").And(app.By.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(20), ignoreException: true).Result;
            button.Should().NotBeNull("사진 폴더에서는 이미지만 보기 제안이 한 줄로 떠야 한다");
            button!.AsButton().Invoke();
            app.Find("모든 파일 보기").IsOffscreen.Should().BeFalse("누르면 이미지만 보기가 켜지고 끄는 단추가 같은 자리에 보인다");
        }
        finally
        {
            try
            {
                Directory.Delete(photos, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
