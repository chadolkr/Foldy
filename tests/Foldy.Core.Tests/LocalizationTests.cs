using System.Globalization;
using System.Text.RegularExpressions;
using Foldy.Core.Localization;

namespace Foldy.Core.Tests;

/// <summary>다국어 (한국어·영어·스페인어·일본어·중국어 간체): 언어 파일이 서로 맞는지, 언어 고르기.</summary>
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

    private static IEnumerable<string> Holes(string s) => HoleRegex().Matches(s).Select(m => m.Value).Distinct();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex HoleRegex();

    [GeneratedRegex(@"[가-힣]")]
    private static partial Regex Hangul();
}
