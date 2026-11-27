using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Foldy.UiTests;

/// <summary>
/// 사용자 의견 (2026-11-27): 새 버전 알림이 아래 한 줄이라 잘 안 보인다 → Notepad++처럼 창으로 "지금 업데이트할까요?"를 묻는다.
/// 가짜 새 버전(FOLDY_TEST_UPDATE)으로 띄워 창이 뜨는지, [나중에]를 누르면 같은 버전은 다시 묻지 않는지 본다. 내려받기·설치는 하지 않는다.
/// </summary>
[Collection("ui")]
public sealed class UpdatePromptTests
{
    private const string Title = "새 버전이 있어요";

    [Fact]
    public void A_ready_update_asks_in_a_window_and_later_is_remembered()
    {
        using var app = FoldyApp.Start(env: new Dictionary<string, string> { ["FOLDY_TEST_UPDATE"] = "99.0.0" });
        var ask = app.FindWindow(Title, 30);
        ask.FindFirstDescendant(app.By.ByName("지금 업데이트")).Should().NotBeNull("[지금 업데이트] 단추가 있어야 한다");
        var later = ask.FindFirstDescendant(app.By.ByName("나중에"));
        later.Should().NotBeNull("[나중에] 단추가 있어야 한다");
        later!.AsButton().Invoke();

        var settings = Path.Combine(app.DataDir, "settings.json");
        Retry.WhileFalse(() => File.Exists(settings) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(settings), "\"updateAskedVersion\":\\s*\"99\\.0\\.0\""),
            TimeSpan.FromSeconds(10), ignoreException: true).Result.Should().BeTrue("물어본 버전을 기억해야 한다");

        // 아래 상태 칸의 단추는 그대로 남아 나중에 누를 수 있다
        Retry.WhileNull(() => app.Main.FindAllDescendants(app.By.ByControlType(ControlType.Button)).FirstOrDefault(b => b.Name.Contains("다시 켜고 업데이트", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10), ignoreException: true).Result.Should().NotBeNull();
    }

    [Fact]
    public void The_same_version_is_not_asked_again()
    {
        using var app = FoldyApp.Start(
            env: new Dictionary<string, string> { ["FOLDY_TEST_UPDATE"] = "99.0.0" },
            prepare: a => File.WriteAllText(Path.Combine(a.DataDir, "settings.json"), "{ \"updateAskedVersion\": \"99.0.0\" }"));

        // 시작 5초 뒤 준비됨 알림 → 단추가 생길 때까지 기다린 뒤에도 창은 없어야 한다
        Retry.WhileNull(() => app.Main.FindAllDescendants(app.By.ByControlType(ControlType.Button)).FirstOrDefault(b => b.Name.Contains("다시 켜고 업데이트", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), ignoreException: true).Result.Should().NotBeNull();
        Thread.Sleep(2000);
        app.App.GetAllTopLevelWindows(app.Automation).Any(w => w.Title == Title).Should().BeFalse();
        app.Main.FindFirstDescendant(app.By.ByName(Title).And(app.By.ByControlType(ControlType.Window))).Should().BeNull();
    }
}
