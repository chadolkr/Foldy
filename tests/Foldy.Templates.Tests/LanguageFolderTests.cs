using System.Text.RegularExpressions;
using Foldy.Core.Desktop;
using Foldy.Core.Localization;
using Foldy.Core.Organize;
using Foldy.Core.Templates;

namespace Foldy.Templates.Tests;

[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection;

/// <summary>
/// 언어별 폴더 이름 (사용자 2026-11-22: "자동 폴더 생성할때 언어에 맞게 폴더명을 만들어주는거지? 5개국어", "언어별로 테스트를 해줘").
/// 만드는 그 시점의 앱 언어로 이름을 붙이고, 예전 한국어 폴더가 있으면 그것을 계속 쓴다.
/// </summary>
[Collection("Loc")]
public sealed partial class LanguageFolderTests
{
    public static TheoryData<string> Others => ["en", "es", "ja", "zh-Hans"];

    [Theory]
    [MemberData(nameof(Others))]
    public void Built_in_templates_make_folders_in_the_app_language(string language)
    {
        using var _ = Use(language);
        using var tmp = new TempDir();
        foreach (var t in TemplateLibrary.LoadBuiltIn())
        {
            Hangul().IsMatch(t.Name).Should().BeFalse($"{language}: 템플릿 이름 {t.Id}: {t.Name}");
            Hangul().IsMatch(t.Description ?? "").Should().BeFalse($"{language}: {t.Id} 설명");
            var values = t.Variables.ToDictionary(v => v.Key, _ => "Sample");
            var plan = TemplatePlanner.Plan(t, tmp.Path, new TemplatePlanOptions { Values = values, Now = new DateTime(2026, 11, 22) }, LocalTemplateFileSystem.Instance);
            var names = plan.Flatten().Select(n => n.Name).ToList();
            names.Should().NotBeEmpty(t.Id);
            names.Where(n => Hangul().IsMatch(n)).Should().BeEmpty($"{language}: {t.Id}의 폴더·파일 이름에 한국어가 남았다");

            // 정리 규칙의 대상 폴더가 트리에 있는 이름과 같아야 한다 (같은 표로 바꾸므로)
            var tops = t.Tree.SelectMany(Flat).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in t.Organize)
            {
                var first = r.Target.Split('/', '\\')[0];
                (tops.Contains(first) || tops.Any(x => x.Split('/').Contains(first))).Should().BeTrue($"{language}: {t.Id} 정리 규칙 대상 '{r.Target}'이 트리에 없다");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Others))]
    public void Wizard_questions_are_translated(string language)
    {
        using var _ = Use(language);
        var json = System.Text.Json.JsonSerializer.Serialize(TemplateLibrary.LoadWizard());
        Hangul().IsMatch(Regex.Unescape(json)).Should().BeFalse($"{language}: 위저드 질문에 한국어가 남았다");
        TemplateCategories.Order.Where(c => Hangul().IsMatch(c)).Should().BeEmpty();
    }

    [Fact]
    public void Korean_keeps_the_original_names()
    {
        using var _ = Use("ko");
        TemplateLibrary.LoadBuiltIn().Single(t => t.Id == "pm-project").Tree.SelectMany(Flat).Select(n => n.Name).Should().Contain(n => n.Contains("01_착수", StringComparison.Ordinal));
        DesktopTidy.ArchiveFolderName.Should().Be(DesktopTidy.ArchiveFolderNameKo);
        FileTypeGroups.FolderName(FileTypeGroups.Documents, null).Should().Be("문서");
    }

    [Fact]
    public void Folders_follow_the_language_but_an_existing_korean_folder_is_reused()
    {
        using var _ = Use("en");
        using var tmp = new TempDir();
        DesktopTidy.ArchiveFolderIn(tmp.Path).Should().Be(Path.Combine(tmp.Path, "Desktop archive"));
        FileTypeGroups.FolderName(FileTypeGroups.Documents, tmp.Path).Should().Be("Documents");

        Directory.CreateDirectory(Path.Combine(tmp.Path, DesktopTidy.ArchiveFolderNameKo));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "문서"));
        DesktopTidy.ArchiveFolderIn(tmp.Path).Should().Be(Path.Combine(tmp.Path, DesktopTidy.ArchiveFolderNameKo), "이미 만든 한국어 폴더는 그대로 쓴다");
        FileTypeGroups.FolderName(FileTypeGroups.Documents, tmp.Path).Should().Be("문서");
    }

    [Fact]
    public void Desktop_tidy_plan_uses_the_language_for_type_folders()
    {
        using var _ = Use("ja");
        using var tmp = new TempDir();
        var desk = Path.Combine(tmp.Path, "desk");
        Directory.CreateDirectory(desk);
        var f = Path.Combine(desk, "report.pdf");
        File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(f, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(f, DateTime.UtcNow.AddDays(-30));
        var plan = DesktopTidy.Plan(desk, DesktopTidy.ArchiveFolderIn(desk), DateTime.UtcNow);
        plan.Should().ContainSingle();
        plan[0].Dst.Should().Contain("デスクトップ保管").And.Contain(Path.DirectorySeparatorChar + "文書" + Path.DirectorySeparatorChar);
    }

    private static IEnumerable<TemplateNode> Flat(TemplateNode n) => new[] { n }.Concat(n.Children.SelectMany(Flat));

    private static Restore Use(string language)
    {
        var before = Loc.Current;
        Loc.Use(language);
        return new Restore(before);
    }

    private sealed class Restore(string before) : IDisposable
    {
        public void Dispose() => Loc.Use(before);
    }

    [GeneratedRegex(@"[가-힣]")]
    private static partial Regex Hangul();
}
