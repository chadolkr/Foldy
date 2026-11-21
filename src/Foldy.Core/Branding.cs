namespace Foldy.Core;

/// <summary>
/// Foldy 한 줄 소개 (사용자 2026-11-21 결정). 중심은 "빠르고 똑똑하다, 인터넷 없이도". AI는 덧붙이는 한 줄.
/// README·도움말·짧은 소개·정보 창·설치 프로그램이 같은 문구를 쓴다. "AI 폴더 관리 도구"라고 부르지 않는다(기능은 규칙 기반, AI는 MCP 연결로만).
/// 글은 언어 파일(assets/i18n)의 tagline·tagline.sub.
/// </summary>
public static class Branding
{
    public static string Tagline => Localization.Loc.T("tagline");

    public static string TaglineSub => Localization.Loc.T("tagline.sub");
}
