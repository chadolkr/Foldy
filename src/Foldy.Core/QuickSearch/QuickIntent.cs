namespace Foldy.Core.QuickSearch;

/// <summary>
/// 빠른 검색에서 말하듯 쳐도 알아듣기 ("다운로드 정리해줘", "메모 쓸래", "휴지통 비워 줘").
/// 밖으로 보내지 않고, 끝말을 다듬고 기능마다 붙인 쉬운 낱말(<see cref="Keywords(string, string?)"/>)로 맞춘다.
/// 새 기능은 명령 목록에 등록하면 저절로 찾히고, 여기에는 사람들이 부를 법한 말만 더한다.
/// </summary>
public static class QuickIntent
{
    // 긴 것부터: 끝말 → 바꿀 말
    private static readonly (string End, string To)[] Endings =
    [
        ("하고 싶어요", ""), ("하고 싶어", ""), ("하고싶어", ""), ("해 주세요", ""), ("해주세요", ""), ("해 줄래", ""), ("해줄래", ""),
        ("해 줘", ""), ("해줘", ""), ("할래요", ""), ("할래", ""), ("하자", ""), ("해요", ""), ("하기", ""),
        ("쓰고 싶어", " 쓰기"), ("쓸래요", " 쓰기"), ("쓸래", " 쓰기"), ("써 줘", " 쓰기"), ("써줘", " 쓰기"),
        ("열어 줘", " 열기"), ("열어줘", " 열기"), ("열래", " 열기"), ("보여 줘", " 보기"), ("보여줘", " 보기"), ("볼래", " 보기"),
        ("비워 줘", " 비우기"), ("비워줘", " 비우기"), ("비울래", " 비우기"), ("지워 줘", " 지우기"), ("지워줘", " 지우기"),
        ("찾아 줘", " 찾기"), ("찾아줘", " 찾기"), ("켜 줘", " 켜기"), ("켜줘", " 켜기"), ("꺼 줘", " 끄기"), ("꺼줘", " 끄기"),
        ("주세요", ""), ("줘", ""),
    ];

    /// <summary>"다운로드 정리해줘" → "다운로드 정리", "메모 쓸래" → "메모 쓰기". 말끝이 없으면 그대로.</summary>
    public static string Normalize(string q)
    {
        var s = q.Trim().TrimEnd('.', '!', '?', '~', ' ');
        s = s.Replace(" 좀 ", " ", StringComparison.Ordinal);
        if (s.StartsWith("좀 ", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        foreach (var (end, to) in Endings)
        {
            if (s.Length > end.Length && s.EndsWith(end, StringComparison.Ordinal))
            {
                s = (s[..^end.Length].TrimEnd() + to).Trim();
                break;
            }
        }

        return s;
    }

    /// <summary>말하듯 쓴 문장인가 (끝말을 다듬었나).</summary>
    public static bool IsSentence(string q) => !string.Equals(Normalize(q), q.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// 기능(명령 id)마다 사람들이 부를 법한 쉬운 말. 언어마다 다르다: 번역 원본 build/i18n/discover_strings.py의 "qi.&lt;id&gt;"
    /// (2026-11-22 기능 찾기: 예전에는 한국어만 있어 다른 언어에서는 기능 이름에만 맞았다).
    /// </summary>
    public static string Keywords(string id, string? language = null)
    {
        var table = Foldy.Core.Localization.Loc.Table(language ?? Foldy.Core.Localization.Loc.Current);
        return table.TryGetValue("qi." + id, out var k) ? k : "";
    }

    /// <summary>기능 하나의 점수: 이름 또는 쉬운 말에 맞으면. 말하듯 쓴 문장은 다듬은 뒤 맞춘다.</summary>
    /// <param name="language">낱말을 고를 언어. 비우면 지금 화면 언어.</param>
    public static int Score(string id, string title, string query, string? language = null)
    {
        var n = Normalize(query);
        var best = Math.Max(QuickSearchMatch.Score(title, query), QuickSearchMatch.Score(title, n));
        if (Keywords(id, language) is { Length: > 0 } k)
        {
            var ks = Math.Max(QuickSearchMatch.Score(k, n), QuickSearchMatch.Score(k, query));
            if (ks > 0)
            {
                best = Math.Max(best, Math.Max(ks, 50)); // 쉬운 말에 맞으면 이름 중간에 맞은 것보다 위
            }
        }

        return best;
    }
}
