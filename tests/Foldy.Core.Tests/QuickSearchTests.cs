using System.Globalization;
using Foldy.Core.QuickSearch;

namespace Foldy.Core.Tests;

public sealed class QuickSearchTests
{
    private static readonly CultureInfo Ko = new("ko-KR");

    [Theory]
    [InlineData("12*3+4", "40")]
    [InlineData("(1+2)/4", "0.75")]
    [InlineData("2^10", "1024")]
    [InlineData("2^3^2", "512")]
    [InlineData("200*15%", "30")]
    [InlineData("1,000 + 2,500", "3500")]
    [InlineData("10 ÷ 4 × 2", "5")]
    [InlineData("-3+5", "2")]
    [InlineData("7-2=", "5")]
    public void Calculates(string q, string copy)
    {
        var a = QuickCalc.Answer(q, Ko);
        Assert.NotNull(a);
        Assert.Equal(copy, a.Copy);
        Assert.Equal("계산", a.Kind);
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("-5")]
    [InlineData("회의록")]
    [InlineData("2026_사업계획서")]
    [InlineData("1/0")]
    [InlineData("(1+2")]
    [InlineData("report-2")]
    public void Ignores_non_expressions(string q) => Assert.Null(QuickCalc.Answer(q, Ko));

    [Theory]
    [InlineData("5 km to mi", "3.106856", "mi")]
    [InlineData("5km mi", "3.106856", "mi")]
    [InlineData("5kmmi", "3.106856", "mi")]
    [InlineData("10 in cm", "25.4", "cm")]
    [InlineData("10 in", "25.4", "cm")]
    [InlineData("32평", "105.785124", "㎡")]
    [InlineData("100F", "37.777778", "°C")]
    [InlineData("100 f to c", "37.777778", "°C")]
    [InlineData("0 c f", "32", "°F")]
    [InlineData("2 GB MB", "2048", "MB")]
    [InlineData("1 lb kg", "0.453592", "kg")]
    [InlineData("3 km는 몇 mi로?", "1.864114", "mi")]
    public void Converts_units(string q, string copy, string unit)
    {
        var a = QuickCalc.Answer(q, Ko);
        Assert.NotNull(a);
        Assert.Equal("단위 변환", a.Kind);
        Assert.Equal(copy, a.Copy);
        Assert.EndsWith(unit, a.Display, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5 km kg")]
    [InlineData("5 apples")]
    [InlineData("km")]
    public void Ignores_bad_units(string q) => Assert.Null(QuickCalc.Convert(q, Ko));

    [Theory]
    [InlineData("설정", "설정", 100)]
    [InlineData("폴더 분석", "폴더", 80)]
    [InlineData("휴지통으로 삭제", "삭제", 60)]
    [InlineData("사이드바 접기/펼치기", "사이드바 펼치기", 30)]
    [InlineData("설정", "ㅅㅈ", 20)]
    [InlineData("폴더 분석", "ㅍㄷㅂ", 20)]
    public void Scores_matches(string title, string q, int score) => Assert.Equal(score, QuickSearchMatch.Score(title, q));

    [Fact]
    public void No_match_is_zero()
    {
        Assert.Equal(0, QuickSearchMatch.Score("설정", "도움말"));
        Assert.Equal(0, QuickSearchMatch.Score("설정", ""));
        Assert.Equal("ㅂㅁㅂㅎ.txt", QuickSearchMatch.Initials("비밀 번호.txt"));
    }

    [Fact]
    public void Settings_catalog_uses_ms_settings_only()
    {
        Assert.NotEmpty(SystemCatalog.SettingsPages);
        Assert.All(SystemCatalog.SettingsPages, p => Assert.StartsWith("ms-settings:", p.Uri, StringComparison.Ordinal));
        Assert.Equal(SystemCatalog.SettingsPages.Count, SystemCatalog.SettingsPages.Select(p => p.Uri).Distinct().Count());
    }

    [Fact]
    public void Start_menu_noise_is_skipped()
    {
        Assert.True(SystemCatalog.IsNoise("Uninstall Foo"));
        Assert.True(SystemCatalog.IsNoise("프로그램 제거"));
        Assert.False(SystemCatalog.IsNoise("메모장"));
        var apps = SystemCatalog.LoadStartApps(); // 실제 시작 메뉴: 오류 없이 읽기만
        Assert.Equal(apps.Count, apps.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public sealed class QuickIntentTests
{
    [Theory]
    [InlineData("다운로드 정리해줘", "다운로드 정리")]
    [InlineData("다운로드 정리 해 주세요", "다운로드 정리")]
    [InlineData("메모 쓸래", "메모 쓰기")]
    [InlineData("휴지통 좀 비워 줘!", "휴지통 비우기")]
    [InlineData("설정", "설정")]
    [InlineData("보고서", "보고서")]
    public void Normalizes_spoken_requests(string q, string expected) => Assert.Equal(expected, QuickIntent.Normalize(q));

    [Theory]
    [InlineData("memo.new", "새 메모 (.md, 지금 폴더에)", "메모 쓸래")]
    [InlineData("cleanup.downloads", "다운로드 정리", "다운로드 정리해줘")]
    [InlineData("app.emptyRecycleBin", "휴지통 비우기…", "휴지통 비워줘")]
    [InlineData("view.zoomIn", "화면 크게 (글씨·버튼)", "글씨 크게 해줘")]
    [InlineData("analysis.open", "폴더 분석 (현재 폴더)", "용량")]
    public void Finds_feature_from_spoken_request(string id, string title, string q) => Assert.True(QuickIntent.Score(id, title, q) >= 50);

    [Fact]
    public void Unrelated_request_does_not_match() => Assert.Equal(0, QuickIntent.Score("memo.new", "새 메모 (.md, 지금 폴더에)", "업데이트 해줘"));
}
