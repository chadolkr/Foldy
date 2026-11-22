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
    public void Finds_feature_from_spoken_request(string id, string title, string q) => Assert.True(QuickIntent.Score(id, title, q, "ko") >= 50);

    [Fact]
    public void Unrelated_request_does_not_match() => Assert.Equal(0, QuickIntent.Score("memo.new", "새 메모 (.md, 지금 폴더에)", "업데이트 해줘", "ko"));

    // 기능 찾기 (2026-11-22): 메뉴 깊숙이·오른쪽 클릭에만 있던 기능도 사람들이 부를 법한 말로 찾힌다
    [Theory]
    [InlineData("file.versions", "덮어쓴 파일 되살리기 (이전 버전, 고른 파일)", "실수로 덮어썼어")]
    [InlineData("file.versions", "덮어쓴 파일 되살리기 (이전 버전, 고른 파일)", "복구해줘")]
    [InlineData("ai.connect", "AI 연결 (Claude 등에서 Foldy로 파일 찾기)", "클로드")]
    [InlineData("view.images", "이미지만 보기 켜기/끄기", "사진 크게 볼래")]
    [InlineData("compare.panes", "두 창 비교·맞추기", "백업 확인")]
    [InlineData("view.preview", "미리보기 창 보이기/숨기기", "열지 않고 보기")]
    public void Finds_hidden_features_in_korean(string id, string title, string q) => Assert.True(QuickIntent.Score(id, title, q, "ko") >= 50, q);

    // 예전에는 쉬운 말이 한국어만 있어 다른 언어 화면에서는 기능 이름에만 맞았다
    [Theory]
    [InlineData("en", "file.versions", "Restore an overwritten file", "overwritten by mistake")]
    [InlineData("en", "analysis.open", "Folder analysis", "disk full")]
    [InlineData("es", "view.zoomIn", "Ampliar", "letra grande")]
    [InlineData("ja", "cleanup.desktop", "デスクトップ整理", "デスクトップを片付ける")]
    [InlineData("zh-Hans", "ai.connect", "连接 AI", "人工智能")]
    public void Finds_features_by_everyday_words_in_every_language(string lang, string id, string title, string q) =>
        Assert.True(QuickIntent.Score(id, title, q, lang) >= 50, $"{lang}: {q}");

    [Fact]
    public void Every_language_has_the_same_feature_words()
    {
        var ko = Foldy.Core.Localization.Loc.Table("ko").Keys.Where(k => k.StartsWith("qi.", StringComparison.Ordinal)).ToList();
        Assert.True(ko.Count >= 35);
        foreach (var lang in Foldy.Core.Localization.Loc.Languages)
        {
            foreach (var k in ko)
            {
                Assert.False(string.IsNullOrWhiteSpace(QuickIntent.Keywords(k[3..], lang.Code)), $"{lang.Code}: {k}");
            }
        }
    }
}

public sealed class DiscoveryHintsTests
{
    private static bool Img(string n) => n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static List<(string, bool)> Files(int images, int others)
    {
        var l = new List<(string, bool)>();
        for (var i = 0; i < images; i++)
        {
            l.Add(($"IMG_{i}.jpg", false));
        }

        for (var i = 0; i < others; i++)
        {
            l.Add(($"doc{i}.txt", false));
        }

        return l;
    }

    [Fact]
    public void Same_named_folders_in_both_panes_suggest_compare() =>
        Assert.Equal(DiscoveryRule.Compare, DiscoveryHints.Pick(@"D:\사진", Files(1, 1), @"E:\백업\사진", false, Img, out _));

    [Theory]
    [InlineData(@"D:\사진", @"D:\사진\")] // 같은 폴더
    [InlineData(@"D:\", @"E:\")] // 드라이브 맨 위
    [InlineData(@"D:\사진", @"E:\문서")] // 이름이 다름
    [InlineData(@"D:\사진", null)]
    public void Compare_needs_two_different_folders_with_the_same_name(string folder, string? other) =>
        Assert.NotEqual(DiscoveryRule.Compare, DiscoveryHints.Pick(folder, Files(0, 1), other, false, Img, out _));

    [Theory]
    [InlineData("node_modules")]
    [InlineData("__history")]
    public void Rebuildable_folders_suggest_the_finder(string name) =>
        Assert.Equal(DiscoveryRule.Rebuildable, DiscoveryHints.Pick(@"C:\work\app", [(name, true), ("src", true)], null, false, Img, out _));

    [Fact]
    public void Bin_alone_is_not_enough_but_bin_and_obj_are()
    {
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick(@"C:\work\app", [("bin", true)], null, false, Img, out _));
        Assert.Equal(DiscoveryRule.Rebuildable, DiscoveryHints.Pick(@"C:\work\app", [("bin", true), ("obj", true)], null, false, Img, out _));
    }

    [Fact]
    public void A_photo_folder_suggests_images_only()
    {
        Assert.Equal(DiscoveryRule.Images, DiscoveryHints.Pick(@"D:\사진", Files(40, 5), null, false, Img, out var n));
        Assert.Equal(40, n);
    }

    [Fact]
    public void Few_or_mixed_photos_or_images_already_on_do_not_suggest()
    {
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick(@"D:\사진", Files(10, 0), null, false, Img, out _));
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick(@"D:\섞임", Files(40, 40), null, false, Img, out _));
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick(@"D:\사진", Files(40, 0), null, true, Img, out _));
    }

    [Fact]
    public void Network_and_sftp_folders_get_no_suggestion()
    {
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick(@"\\server\share\사진", Files(40, 0), null, false, Img, out _));
        Assert.Equal(DiscoveryRule.None, DiscoveryHints.Pick("sftp://host/home/사진", Files(40, 0), null, false, Img, out _));
    }
}
