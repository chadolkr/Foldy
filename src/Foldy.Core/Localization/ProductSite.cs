namespace Foldy.Core.Localization;

/// <summary>
/// Foldy 소개 페이지 (GitHub Pages, site/). 정보 창과 도움말 메뉴에서 연다 (사용자 요청 2026-11-25).
/// 사이트는 한국어가 맨 위 주소, 다른 언어는 /en/·/es/·/ja/·/zh-cn/ 아래에 있다 (site/src/i18n/index.ts).
/// </summary>
public static class ProductSite
{
    /// <summary>소개 페이지 맨 위 (한국어).</summary>
    public const string Home = "https://chadolkr.github.io/Foldy/";

    /// <summary>앱 언어에 맞는 소개 페이지. 모르는 언어는 영어 페이지.</summary>
    public static string For(string? language) => language switch
    {
        "ko" => Home,
        "es" or "ja" or "en" => Home + language + "/",
        "zh-Hans" => Home + "zh-cn/",
        _ => Home + "en/",
    };

    /// <summary>지금 쓰는 언어의 소개 페이지.</summary>
    public static string Current => For(Loc.Current);
}
