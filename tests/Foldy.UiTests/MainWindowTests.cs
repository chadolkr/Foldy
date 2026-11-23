using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Foldy.UiTests;

// UI 테스트는 화면 하나를 같이 쓰므로 한 번에 하나씩
[CollectionDefinition("ui", DisableParallelization = true)]
public sealed class UiCollection;

[Collection("ui")]
public sealed partial class MainWindowTests
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

    /// <summary>
    /// 사용자 2026-11-22 "메모 잠금, 금고 기능이 직관적이지 않아": [지키기] 메뉴는 무엇을 하는지 쉬운 말로 보이고,
    /// 고른 폴더가 없을 때 [폴더를 금고에 넣기]를 누르면 상태 줄에 작게 쓰는 대신 폴더 고르기 창이 바로 뜬다.
    /// </summary>
    [Fact]
    public void Protect_menu_explains_each_item_and_asks_for_a_folder_when_none_is_selected()
    {
        using var app = FoldyApp.Start();
        app.Find("지키기").AsButton().Invoke();
        AutomationElement? Item(string name) => Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName(name).And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        Item("🔒 잠긴 메모 쓰기 · 비밀번호 같은 글 적어 두기").Should().NotBeNull("잠긴 메모가 무엇에 쓰는 것인지 메뉴에서 보여야 한다");
        Item("🔒 파일 잠그기 · 암호를 넣어야 열려요").Should().NotBeNull();
        var vault = Item("🔒 폴더를 금고에 넣기 · 폴더째 잠가요");
        vault.Should().NotBeNull();
        vault!.AsMenuItem().Invoke();

        var pick = app.FindWindow("금고에 넣을 폴더를 골라 주세요", 15);
        pick.Should().NotBeNull("고른 폴더가 없으면 폴더 고르기 창이 떠야 한다");
        pick.Close();
    }

    /// <summary>새 잠긴 메모: 암호를 정하는 칸에 길이 제한(8글자 이상)을 미리 보여 준다 (제한은 미리 알리기).</summary>
    [Fact]
    public void New_locked_note_shows_the_password_rule_before_typing()
    {
        using var app = FoldyApp.Start();
        app.Find("지키기").AsButton().Invoke();
        var item = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("🔒 잠긴 메모 쓰기 · 비밀번호 같은 글 적어 두기").And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        item!.AsMenuItem().Invoke();

        var guide = app.FindWindow("새 잠긴 메모", 15);
        app.Find("암호 (8글자 이상)", guide).Should().NotBeNull();
        app.Find("같은 암호를 한 번 더", guide).Should().NotBeNull();
        app.Find("그만두기", guide).AsButton().Invoke();
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    /// <summary>사용자 제보(2026-11-22): 잠긴 메모를 저장하고 닫으면 Foldy 창이 다른 창 뒤로 내려가 안 보였다. 닫은 뒤 Foldy 창이 맨 앞이어야 한다.</summary>
    [Fact]
    public void Closing_a_locked_note_brings_the_Foldy_window_back_to_front()
    {
        using var app = FoldyApp.Start();
        app.Find("지키기").AsButton().Invoke();
        var item = Retry.WhileNull(
            () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("🔒 잠긴 메모 쓰기 · 비밀번호 같은 글 적어 두기").And(app.By.ByControlType(ControlType.MenuItem))),
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        item!.AsMenuItem().Invoke();

        var guide = app.FindWindow("새 잠긴 메모", 15);
        var boxes = Retry.WhileEmpty(() => guide.FindAllDescendants(app.By.ByControlType(ControlType.Edit)), TimeSpan.FromSeconds(10), ignoreException: true).Result;
        boxes.Should().HaveCountGreaterThanOrEqualTo(2);
        foreach (var b in boxes.Take(2))
        {
            // 암호 칸: 값 넣기(ValuePattern)가 되면 그것으로, 안 되면 눌러서 입력
            var set = false;
            try
            {
                if (b.Patterns.Value.TryGetPattern(out var v))
                {
                    v.SetValue("ui-test-pass-1234");
                    set = true;
                }
            }
            catch (Exception)
            {
                // 암호 칸은 값 넣기를 막을 수 있다 → 아래에서 직접 입력
            }

            if (!set)
            {
                b.Click();
                FlaUI.Core.Input.Keyboard.Type("ui-test-pass-1234");
                FlaUI.Core.Input.Wait.UntilInputIsProcessed();
            }
        }

        app.Find("메모 만들기", guide).AsButton().Invoke();
        const string memoTitle = "새 메모 - 잠긴 메모";
        var memo = Retry.WhileNull(
            () => app.App.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title == memoTitle)
                  ?? app.Automation.GetDesktop().FindFirstDescendant(app.By.ByControlType(ControlType.Window).And(app.By.ByName(memoTitle)))?.AsWindow(),
            TimeSpan.FromSeconds(15), ignoreException: true).Result;
        var said = memo is null && !guide.IsOffscreen
            ? string.Join(" / ", guide.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Select(t => t.Name))
            : "";
        memo.Should().NotBeNull("암호를 정하면 메모 창이 열린다. 안내 창: " + said);
        app.Find("잠그고 닫기", memo).AsButton().Invoke();

        var front = Retry.WhileFalse(() => GetForegroundWindow() == app.Main.Properties.NativeWindowHandle.Value, TimeSpan.FromSeconds(5), ignoreException: true).Result;
        front.Should().BeTrue("메모를 닫으면 Foldy 창이 맨 앞에 보여야 한다");
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
    /// 2026-11-22 사용자: "ctrl+space → 제어판 프로그램 추가/삭제 등 검색이 안 보인다. 윈도우 검색처럼 그룹핑이 되어야".
    /// 제어판 항목이 설정 묶음에 나오고, 위의 보기 단추로 한 묶음만 볼 수 있다. (열지는 않는다)
    /// 결과 줄(Border)은 UI 자동화에 드러나지 않아, 줄 안의 제목 글자로 찾는다 (계산 시험과 같은 방식).
    /// </summary>
    [Fact]
    public void Quick_search_finds_control_panel_and_filters_by_group()
    {
        using var app = FoldyApp.Start();
        AutomationElement? hit = null;
        Window? qs = null;
        for (var attempt = 0; attempt < 3 && hit is null; attempt++)
        {
            try
            {
                app.Find("찾기").AsButton().Invoke();
                var item = Retry.WhileNull(
                    () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("빠른 검색 (파일·앱·설정·기능·계산)").And(app.By.ByControlType(ControlType.MenuItem))),
                    TimeSpan.FromSeconds(10), ignoreException: true).Result;
                item!.AsMenuItem().Invoke();
                qs = app.FindWindow("Foldy 빠른 검색", 15);
                Thread.Sleep(500);
                qs.FindFirstDescendant(app.By.ByControlType(ControlType.Edit))!.AsTextBox().Text = "프로그램 추가/삭제";
                hit = Retry.WhileNull(() => qs.FindFirstDescendant(app.By.ByName("프로그램 추가/제거")), TimeSpan.FromSeconds(5), ignoreException: true).Result;
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException or NullReferenceException)
            {
                Thread.Sleep(1000);
            }
        }

        hit.Should().NotBeNull("제어판의 프로그램 추가/제거가 설정 묶음에 나와야 한다");
        qs!.FindFirstDescendant(app.By.ByName("보기: 앱"))!.AsButton().Invoke();
        Retry.WhileTrue(() => qs.FindFirstDescendant(app.By.ByName("프로그램 추가/제거")) is not null, TimeSpan.FromSeconds(5), ignoreException: true);
        qs.FindFirstDescendant(app.By.ByName("프로그램 추가/제거")).Should().BeNull("앱만 보면 설정은 숨는다");
        qs.FindFirstDescendant(app.By.ByName("보기: 설정"))!.AsButton().Invoke();
        Retry.WhileNull(() => qs.FindFirstDescendant(app.By.ByName("프로그램 추가/제거")), TimeSpan.FromSeconds(5), ignoreException: true)
            .Result.Should().NotBeNull("설정만 보면 다시 나온다");
        // 2026-11-23 사용자: "전체가 몇 건인지 알 수 있게" — 묶음 이름에 찾은 수가 붙는다 ("설정 · 3개")
        qs.FindAllDescendants().Any(e => { try { return e.Name.StartsWith("설정 · ", StringComparison.Ordinal) && e.Name.EndsWith('개'); } catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return false; } catch (System.Runtime.InteropServices.COMException) { return false; } })
            .Should().BeTrue("묶음 이름 옆에 찾은 수가 보여야 한다");
        qs.FindFirstDescendant(app.By.ByName("열기")).Should().NotBeNull("오른쪽에 [열기] 단추가 있어야 한다");
        qs.Close();
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

    /// <summary>사용자 2026-11-23: 주 창의 한쪽 창만 크게 보고, 같은 단추로 다시 두 창으로.</summary>
    [Fact]
    public void One_pane_can_fill_the_window_and_come_back()
    {
        using var app = FoldyApp.Start();
        AutomationElement[] Buttons() => app.Main.FindAllDescendants(app.By.ByAutomationId("PaneSingleButton"));
        Retry.WhileFalse(() => Buttons().Length == 2, TimeSpan.FromSeconds(15), ignoreException: true);
        Buttons().Should().HaveCount(2, "두 창 모두에 [이 창만 크게]가 있다");
        Buttons()[0].AsButton().Invoke();
        Retry.WhileFalse(() => Buttons().Length == 1, TimeSpan.FromSeconds(5), ignoreException: true);
        var only = Buttons().Should().ContainSingle("다른 창은 접힌다").Subject;
        only.Name.Should().Be("두 창으로 보기", "켜고 끄기는 같은 자리");
        only.AsButton().Invoke();
        Retry.WhileFalse(() => Buttons().Length == 2, TimeSpan.FromSeconds(5), ignoreException: true);
        Buttons().Should().HaveCount(2, "같은 단추로 다시 두 창");
    }

    /// <summary>
    /// 사용자 2026-11-23: 폴더 정리의 같은 파일은 어느 폴더의 파일과 같은지 전체 경로로 보이고, 남길 파일을 고를 수 있다.
    /// </summary>
    [Fact]
    public void Organize_shows_where_identical_files_are_and_lets_you_pick_the_one_to_keep()
    {
        var root = Path.Combine(Path.GetTempPath(), "foldy-dups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        var first = Path.Combine(root, "a", "보고서.txt");
        var second = Path.Combine(root, "b", "보고서.txt");
        File.WriteAllText(first, "같은 내용");
        File.WriteAllText(second, "같은 내용");
        File.SetLastWriteTimeUtc(first, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        try
        {
            using var app = FoldyApp.Start(openPath: root);
            app.Find("정리하기").AsButton().Invoke();
            Retry.WhileNull(
                () => app.Automation.GetDesktop().FindFirstDescendant(app.By.ByName("이 폴더 정리하기…").And(app.By.ByControlType(ControlType.MenuItem))),
                TimeSpan.FromSeconds(10), ignoreException: true).Result!.AsMenuItem().Invoke();
            var win = app.FindWindow("폴더 정리", 15);
            app.Find("하위 폴더 안까지 정리", win).AsCheckBox().IsChecked = true;
            app.Find("계획 만들기", win).AsButton().Invoke();

            var tab = Retry.WhileNull(() => win.FindFirstDescendant(app.By.ByAutomationId("OrganizeDuplicatesTab")), TimeSpan.FromSeconds(30), ignoreException: true).Result;
            tab.Should().NotBeNull("같은 파일이 있으면 '같은 파일' 탭이 보여야 한다");
            tab!.AsTabItem().Select();
            var sets = Retry.WhileNull(() => win.FindFirstDescendant(app.By.ByAutomationId("OrganizeDuplicateSets")), TimeSpan.FromSeconds(10), ignoreException: true).Result!;
            Retry.WhileNull(() => sets.FindFirstDescendant(app.By.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10), ignoreException: true).Result!
                .AsListBoxItem().Select();

            RadioButton Keep(string path) => Retry.WhileNull(
                () => win.FindFirstDescendant(app.By.ByName(path).And(app.By.ByControlType(ControlType.RadioButton))), TimeSpan.FromSeconds(10), ignoreException: true).Result!.AsRadioButton();
            var keepFirst = Keep(first);
            var keepSecond = Keep(second);
            keepFirst.IsChecked.Should().BeTrue("처음에는 가장 오래된 파일을 남긴다");
            keepSecond.IsChecked.Should().BeFalse();
            keepSecond.IsChecked = true;
            Retry.WhileFalse(() => keepFirst.IsChecked == false, TimeSpan.FromSeconds(5), ignoreException: true);
            keepSecond.IsChecked.Should().BeTrue("고른 파일을 남긴다");
            keepFirst.IsChecked.Should().BeFalse("남길 파일은 하나만");
            File.Exists(first).Should().BeTrue("미리보기에서는 아무것도 옮기거나 지우지 않는다");
            win.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
