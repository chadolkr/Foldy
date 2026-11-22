using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Foldy.UiTests;

/// <summary>
/// 언어별 화면 시험 (사용자 2026-11-22: "언어별로 테스트를 해줘").
/// 한국어가 아닌 네 언어로 Foldy를 띄워 주 창·설정 창·빠른 검색 창에 보이는 글(화면 낭독기 이름·도움말)에 한국어가 남았는지 본다.
/// 파일·폴더 이름처럼 사용자 자료나 Windows가 주는 이름(내 PC 폴더 이름 등)은 뺀다.
/// 찾은 글은 실패 메시지와 TestResults/language-&lt;언어&gt;.txt에 남는다.
/// </summary>
[Collection("ui")]
public sealed partial class LanguageTests
{
    public static TheoryData<string> Languages => ["en", "es", "ja", "zh-Hans"];

    [Theory]
    [MemberData(nameof(Languages))]
    public void Screens_have_no_korean_left(string language)
    {
        var sample = Path.Combine(Path.GetTempPath(), "foldy-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sample, "Reports"));
        File.WriteAllText(Path.Combine(sample, "notes.txt"), "test");
        File.WriteAllText(Path.Combine(sample, "photo.jpg"), "x");
        try
        {
            using var app = FoldyApp.Start(openPath: sample, language: language);
            Retry.WhileNull(() => app.Main.FindFirstDescendant(app.By.ByClassName("FastFileList")), TimeSpan.FromSeconds(20), ignoreException: true);
            Thread.Sleep(1500); // 목록·상태 줄이 그려질 때까지

            var found = new SortedSet<string>(StringComparer.Ordinal);
            var seen = new List<string>();
            Collect(app.Main, "main", found, seen);

            // 설정 창: 사이드바 설정 단추 (이름은 그 언어로)
            app.Find(Text(language, "shell.settings")).AsButton().Invoke();
            var settings = Retry.WhileNull(
                () => app.App.GetAllTopLevelWindows(app.Automation).FirstOrDefault(w => w.Title != app.Main.Title)
                      ?? app.Main.FindFirstChild(app.By.ByControlType(ControlType.Window))?.AsWindow(),
                TimeSpan.FromSeconds(10), ignoreException: true).Result;
            if (settings is not null)
            {
                Thread.Sleep(800);
                Collect(settings, "settings", found, seen);
                Shot(settings, language, "settings");
                settings.Close();
            }
            else
            {
                found.Add("[settings] 설정 창이 열리지 않음");
            }

            Report(language, found, seen);
            found.Should().BeEmpty($"{language} 화면에 한국어가 남아 있다");
        }
        finally
        {
            try
            {
                Directory.Delete(sample, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>사용자 자료·Windows가 주는 이름은 뺀다: 파일 목록·트리 항목, 경로 칸.</summary>
    private static void Collect(AutomationElement root, string where, SortedSet<string> found, List<string> seen)
    {
        foreach (var e in root.FindAllDescendants())
        {
            ControlType type;
            string name, help;
            try
            {
                type = e.ControlType;
                if (type is ControlType.TreeItem or ControlType.Edit or ControlType.Document or ControlType.TitleBar)
                {
                    continue;
                }

                if (IsWindowsChrome(e, root))
                {
                    continue; // 제목 표시줄 단추·시스템 메뉴는 Windows가 그 PC 언어로 그린다
                }

                name = e.Name ?? "";
                help = e.HelpText ?? "";
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.PropertyNotSupportedException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                continue;
            }

            foreach (var text in new[] { name, help })
            {
                if (text.Length > 0)
                {
                    seen.Add($"[{where}] {type}: {text.Replace('\n', ' ')}" + (Hangul().IsMatch(text) ? " <= " + Chain(e, root) : ""));
                }

                if (Hangul().IsMatch(text) && !IsUserData(e))
                {
                    found.Add($"[{where}] {type}: {text.Replace('\n', ' ')}");
                }
            }
        }
    }

    private static string Chain(AutomationElement e, AutomationElement root)
    {
        var parts = new List<string>();
        for (var p = e.Parent; p is not null && !p.Equals(root); p = p.Parent)
        {
            parts.Add($"{p.ControlType}#{p.AutomationId}");
        }

        return string.Join(" < ", parts);
    }

    /// <summary>파일·폴더 항목 안의 글 (이름 칸 등)과 사이드바의 Windows 폴더 이름.</summary>
    private static bool IsUserData(AutomationElement e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
        {
            try
            {
                if (p.ControlType is ControlType.TreeItem)
                {
                    return true;
                }

                if (p.AutomationId is "QuickAccessList" or "DriveList" or "FavoritesList")
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FlaUI.Core.Exceptions.PropertyNotSupportedException or System.Runtime.InteropServices.COMException or FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>그 언어의 화면 글 (assets/i18n/&lt;언어&gt;.json).</summary>
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

    private static bool IsWindowsChrome(AutomationElement e, AutomationElement root)
    {
        if (e.AutomationId is "SystemMenuBar" or "Item 1" or "Minimize" or "Maximize" or "Close" or "Restore")
        {
            return true;
        }

        for (var p = e.Parent; p is not null && !p.Equals(root); p = p.Parent)
        {
            if (p.ControlType == ControlType.TitleBar || p.AutomationId == "SystemMenuBar")
            {
                return true;
            }
        }

        return false;
    }

    private static void Shot(AutomationElement window, string language, string what)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(dir);
            FlaUI.Core.Capturing.Capture.Element(window).ToFile(Path.Combine(dir, $"language-{language}-{what}.png"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // 화면 사진은 검수용: 못 찍어도 시험은 계속
        }
    }

    private static void Report(string language, IEnumerable<string> found, IEnumerable<string> seen)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"language-{language}.txt"), found);
        File.WriteAllLines(Path.Combine(dir, $"language-{language}-all.txt"), seen.Distinct()); // 검수용: 본 글 전부 (글자 잘림·어색한 번역 확인)
    }

    [GeneratedRegex("[가-힣]")]
    private static partial Regex Hangul();
}
