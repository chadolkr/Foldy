namespace Foldy.Core.QuickSearch;

/// <summary>
/// 빠른 검색의 이름 맞추기: 이름에 그대로 들어 있거나, 띄어 쓴 낱말이 모두 들어 있거나, 한글 초성이 맞으면(ㅅㅈ → 설정) 맞다.
/// 점수가 클수록 위에 (처음부터 같음 &gt; 낱말 시작 &gt; 안에 있음 &gt; 초성).
/// </summary>
public static class QuickSearchMatch
{
    private static readonly char[] Choseong =
    [
        'ㄱ', 'ㄲ', 'ㄴ', 'ㄷ', 'ㄸ', 'ㄹ', 'ㅁ', 'ㅂ', 'ㅃ', 'ㅅ', 'ㅆ', 'ㅇ', 'ㅈ', 'ㅉ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ',
    ];

    public static bool Matches(string title, string query) => Score(title, query) > 0;

    /// <summary>0이면 안 맞음.</summary>
    public static int Score(string title, string query)
    {
        query = query.Trim();
        if (query.Length == 0 || string.IsNullOrEmpty(title))
        {
            return 0;
        }

        var cmp = StringComparison.CurrentCultureIgnoreCase;
        if (title.Equals(query, cmp))
        {
            return 100;
        }

        if (title.StartsWith(query, cmp))
        {
            return 80;
        }

        var i = title.IndexOf(query, cmp);
        if (i > 0)
        {
            return char.IsWhiteSpace(title[i - 1]) || title[i - 1] is '(' or '·' or '-' or '_' ? 60 : 40;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => title.Contains(w, cmp)))
        {
            return 30;
        }

        if (query.All(IsChoseong) && Initials(title).Contains(query, StringComparison.Ordinal))
        {
            return 20;
        }

        return 0;
    }

    private static readonly char[] WordBreaks = [' ', '/', ',', '·', '+', '&', '\t'];

    /// <summary>
    /// 쉬운 말 목록에 맞추기: 친 말을 띄어쓰기·"/"·","로 나눠, 나눈 낱말이 모두 <paramref name="words"/> 어딘가에 있으면 맞다
    /// ("프로그램 추가/삭제" → 프로그램·추가·삭제). 낱말 여럿이 다 맞으면 45, 하나면 35. 0이면 안 맞음.
    /// </summary>
    public static int ScoreWords(string words, string query)
    {
        if (string.IsNullOrEmpty(words))
        {
            return 0;
        }

        var parts = query.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        var cmp = StringComparison.CurrentCultureIgnoreCase;
        return parts.All(p => words.Contains(p, cmp)) ? (parts.Length > 1 ? 45 : 35) : 0;
    }

    /// <summary>한글 음절은 초성으로, 나머지는 그대로 (공백 제외).</summary>
    public static string Initials(string s)
    {
        var chars = new List<char>(s.Length);
        foreach (var c in s)
        {
            if (c is >= '가' and <= '힣')
            {
                chars.Add(Choseong[(c - '가') / (21 * 28)]);
            }
            else if (!char.IsWhiteSpace(c))
            {
                chars.Add(c);
            }
        }

        return new string([.. chars]);
    }

    private static bool IsChoseong(char c) => Array.IndexOf(Choseong, c) >= 0;
}
