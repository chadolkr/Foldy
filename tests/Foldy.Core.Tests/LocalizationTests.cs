using System.Globalization;
using System.Text.RegularExpressions;
using Foldy.Core.Localization;

namespace Foldy.Core.Tests;

/// <summary>다국어 (한국어·영어·스페인어·일본어·중국어 간체): 언어 파일이 서로 맞는지, 언어 고르기.</summary>
[Collection("Loc")]
public sealed partial class LocalizationTests
{
    [Fact]
    public void Every_language_has_every_key_and_the_same_placeholders()
    {
        var en = Loc.Table("en");
        en.Should().NotBeEmpty();
        foreach (var lang in Loc.Languages)
        {
            var t = Loc.Table(lang.Code);
            t.Keys.Should().BeEquivalentTo(en.Keys, lang.Code);
            foreach (var (key, value) in t)
            {
                value.Should().NotBeNullOrWhiteSpace($"{lang.Code}:{key}");
                Holes(value).Should().BeEquivalentTo(Holes(en[key]), $"{lang.Code}:{key}의 {{0}} 자리가 영어와 같아야 한다");
                if (lang.Code != "ko")
                {
                    Hangul().IsMatch(value).Should().BeFalse($"{lang.Code}:{key}에 한국어가 남아 있다: {value}");
                }
            }
        }
    }

    [Theory]
    [InlineData(null, "ko-KR", "ko")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "es-MX", "es")]
    [InlineData("auto", "ja-JP", "ja")]
    [InlineData("auto", "zh-CN", "zh-Hans")]
    [InlineData("auto", "zh-TW", "en")] // 번체는 아직 없어 영어
    [InlineData("auto", "fr-FR", "en")] // 지원하지 않는 언어는 영어
    [InlineData("ja", "ko-KR", "ja")] // 설정이 Windows보다 먼저
    [InlineData("xx", "ko-KR", "ko")] // 모르는 설정 값은 Windows 언어로
    public void Language_follows_windows_unless_chosen(string? setting, string windows, string expected) =>
        Loc.Resolve(setting, CultureInfo.GetCultureInfo(windows)).Should().Be(expected);

    [Fact]
    public void Missing_key_falls_back_to_english_then_to_the_key()
    {
        var before = Loc.Current;
        try
        {
            Loc.Use("ko");
            Loc.T("tagline").Should().Be("빠르고 똑똑한 파일 관리자, 오프라인에서도");
            Loc.T("no.such.key").Should().Be("no.such.key");
            Loc.F("suggest.desktop", 27).Should().Contain("27");
        }
        finally
        {
            Loc.Use(before);
        }
    }

    /// <summary>
    /// 코드와 XAML이 쓰는 키(Loc.T("…"), Loc.F("…"), {loc:T …})가 모두 언어 파일에 있어야 한다.
    /// 언어 파일은 build/i18n/strings.py로 다시 만들어지므로, JSON만 고친 키는 다음에 사라진다 (0.9.36에서 [건너뛰기]가 사라졌던 일).
    /// </summary>
    [Fact]
    public void Every_key_used_in_code_is_in_the_language_files()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("저장소 루트(Foldy.slnx)를 찾아야 한다");
        var src = Path.Combine(dir!.FullName, "src");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var regex = f.EndsWith(".cs", StringComparison.Ordinal) ? CodeKey() : f.EndsWith(".xaml", StringComparison.Ordinal) ? XamlKey() : null;
            if (regex is null)
            {
                continue;
            }

            foreach (Match m in regex.Matches(File.ReadAllText(f)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        used.Should().NotBeEmpty();
        var ko = Loc.Table("ko");
        used.Where(k => !k.EndsWith('.') && !ko.ContainsKey(k)).Should().BeEmpty("strings.py 표에 넣고 python build/i18n/strings.py로 언어 파일을 다시 만들어야 한다");
    }

    /// <summary>언어별 설명서(assets/help/index.&lt;언어&gt;.md)의 그림이 다 있어야 하고, 한국어 말고는 본문에 한국어가 거의 없어야 한다.</summary>
    [Fact]
    public void Help_files_point_to_existing_images()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        var help = Path.Combine(dir!.FullName, "assets", "help");
        var files = Directory.GetFiles(help, "index*.md");
        files.Should().Contain(f => Path.GetFileName(f) == "index.md");
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (Match m in ImageLink().Matches(text))
            {
                File.Exists(Path.Combine(help, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue($"{Path.GetFileName(f)}: 그림 {m.Groups[1].Value}");
            }

            if (Path.GetFileName(f) != "index.md")
            {
                // 예전 한국어 폴더 이름 같은 몇 곳만 남는다
                Hangul().Count(text).Should().BeLessThan(80, $"{Path.GetFileName(f)}에 한국어가 많이 남았다");
            }
        }
    }

    /// <summary>
    /// 짧은 소개(쇼츠) 장면 그림마다 다가갈 곳(&lt;그림&gt;.focus.json)이 있어야 한다 (사용자 2026-11-22: 기계적으로 가운데를 키우지 말 것).
    /// 문서 캡처가 실제 화면 요소 위치로 적는다. 언어별 폴더에도 같이 있어야 그 언어 화면의 위치로 다가간다.
    /// </summary>
    [Fact]
    public void Short_intro_images_have_a_focus_area()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Foldy.slnx")))
        {
            dir = dir.Parent;
        }

        var images = Path.Combine(dir!.FullName, "assets", "help", "images");
        string[] scenes = ["quicksearch", "quicksearch-settings", "quicksearch-calc", "quicksearch-talk", "analysis", "rebuildable", "guide", "para-organize", "para-template", "main", "mcp", "memo", "git-changes"];
        foreach (var folder in new[] { images }.Concat(Directory.GetDirectories(images)))
        {
            foreach (var s in scenes.Where(s => File.Exists(Path.Combine(folder, s + ".png"))))
            {
                var focus = Path.Combine(folder, s + ".focus.json");
                File.Exists(focus).Should().BeTrue($"{focus}가 있어야 한다 (build/doc-screenshots.ps1로 다시 찍기)");
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(focus)).RootElement;
                var (x, y, w, h) = (json.GetProperty("x").GetDouble(), json.GetProperty("y").GetDouble(), json.GetProperty("w").GetDouble(), json.GetProperty("h").GetDouble());
                (w > 0.01 && h > 0.01 && x >= 0 && y >= 0 && x + w <= 1.001 && y + h <= 1.001).Should().BeTrue(focus);
                (w * h).Should().BeLessThan(0.95, $"{focus}: 그림 전체를 가리키면 다가가는 의미가 없다");
            }
        }
    }

    [GeneratedRegex(@"!\[[^\]]*\]\((images/[^)]+)\)")]
    private static partial Regex ImageLink();

    [GeneratedRegex(@"Loc\.[TF]\(""([^""]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{loc:T ([^}\s]+)")]
    private static partial Regex XamlKey();

    private static IEnumerable<string> Holes(string s) => HoleRegex().Matches(s).Select(m => m.Value).Distinct();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex HoleRegex();

    [GeneratedRegex(@"[가-힣]")]
    private static partial Regex Hangul();
}

/// <summary>언어를 바꿔 보는 시험은 다른 시험과 동시에 돌지 않는다 (Loc.Current는 하나).</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection
{
}
