using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Foldy.UiTests;

/// <summary>
/// 화면 글이 잘리거나 밀려서 안 보이는 곳이 없어야 한다 (사용자 2026-11-25: 쉬운 말로 바꾸며 "단어나 문장 길이가 길어진 경우 화면 디자인이 밀려서 안보이는게 있는지 점검").
/// 다섯 언어로 Foldy를 띄워 주 창과 설정 창의 모든 쪽을 열고, Foldy 안의 점검기(FOLDY_CUT_TEXT_REPORT)가 찾은 잘린 글을 본다.
/// 찾은 것은 실패 메시지와 TestResults/cut-text-&lt;언어&gt;.txt에 남는다.
/// </summary>
[Collection("ui")]
public sealed class CutTextTests
{
    public static TheoryData<string> Languages => ["ko", "en", "es", "ja", "zh-Hans"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Screen_text_is_not_cut(string language)
    {
        using var app = FoldyApp.Start(language: language, reportCutText: true);
        Retry.WhileNull(() => app.Main.FindFirstDescendant(app.By.ByClassName("FastFileList")), TimeSpan.FromSeconds(20), ignoreException: true);
        Thread.Sleep(3500); // 점검기는 1.5초마다 본다

        app.Find(Text(language, "shell.settings")).AsButton().Invoke();
        var settings = Retry.WhileNull(
            () => app.App.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title != app.Main.Title)
                  ?? app.Main.FindFirstChild(app.By.ByControlType(ControlType.Window))?.AsWindow(),
            TimeSpan.FromSeconds(10), ignoreException: true).Result;
        settings.Should().NotBeNull("설정 창이 열려야 한다");
        var pages = Retry.WhileNull(() => settings!.FindFirstDescendant(app.By.ByAutomationId("Categories")), TimeSpan.FromSeconds(10), ignoreException: true).Result;
        pages.Should().NotBeNull("설정 분류 목록");
        foreach (var page in pages!.FindAllChildren(app.By.ByControlType(ControlType.ListItem)))
        {
            page.AsListBoxItem().Select();
            Thread.Sleep(2000);
        }

        settings!.Close();
        Thread.Sleep(500);

        var found = File.Exists(app.CutTextReport) ? File.ReadAllLines(app.CutTextReport) : Array.Empty<string>();
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"cut-text-{language}.txt"), found);
        File.Exists(app.CutTextReport).Should().BeTrue("Foldy 안의 점검기가 돌아야 한다 (FOLDY_CUT_TEXT_REPORT)");
        found.Should().BeEmpty($"{language} 화면에 잘리거나 밀려서 안 보이는 글이 있다");
    }

    private static string Text(string language, string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        var json = File.ReadAllText(Path.Combine(dir!.FullName, "assets", "i18n", language + ".json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(key).GetString()!;
    }
}
