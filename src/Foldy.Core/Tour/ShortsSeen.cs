namespace Foldy.Core.Tour;

/// <summary>
/// 짧은 소개(쇼츠)를 기능별로 기억한다 (사용자 2026-11-22 "기능 쇼츠를 나누어서 이미 본것은 안보게 새로운 것은 쇼츠를 보게").
/// 본 장면의 이름(tour.qs, tour.space …)을 한 줄에 하나씩 적어 두고, 업데이트 뒤에는 아직 안 본 장면만 한 번 보여 준다.
/// 건너뛰어도 본 것으로 친다(다시 보려면 ? → 짧은 소개 보기).
/// </summary>
public static class ShortsSeen
{
    /// <summary>
    /// 장면별로 기억하기 전(1.2.0까지)에 있던 장면. 예전 기록(tour-shown.txt)만 있는 사람은 이것을 모두 본 것으로 친다.
    /// 이 목록은 바꾸지 않는다. 새 장면은 여기에 넣지 말고 장면 목록에만 더하면 업데이트 뒤 그것만 보인다.
    /// </summary>
    public static readonly IReadOnlyList<string> Before121 =
    [
        "tour.qs", "tour.qs2", "tour.qs3", "tour.qs4", "tour.space", "tour.junk", "tour.desk",
        "tour.para", "tour.tpl", "tour.copy", "tour.ai", "tour.memo", "tour.git",
    ];

    /// <summary>시험용 기록: 모두 본 것으로 친다.</summary>
    public const string Always = "always";

    /// <summary>
    /// 아직 안 본 장면 (순서는 <paramref name="all"/> 그대로).
    /// <paramref name="oldStamp"/>는 예전 기록(tour-shown.txt) 내용, <paramref name="seen"/>은 본 장면 기록(tour-seen.txt)의 줄. 없으면 null.
    /// </summary>
    public static IReadOnlyList<string> Unseen(IReadOnlyList<string> all, string? oldStamp, IEnumerable<string>? seen)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (string.Equals(oldStamp?.Trim(), Always, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        if (seen is not null)
        {
            foreach (var line in seen)
            {
                var key = line.Trim();
                if (key.Length > 0)
                {
                    known.Add(key);
                }
            }
        }

        // 장면별 기록이 생기기 전에 소개를 본 사람: 그때 있던 장면은 모두 봤다
        if (!string.IsNullOrWhiteSpace(oldStamp))
        {
            known.UnionWith(Before121);
        }

        return [.. all.Where(k => !known.Contains(k))];
    }

    /// <summary>본 장면 기록에 더할 줄 (이미 있는 것은 빼고, 순서 유지).</summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string>? seen, IEnumerable<string> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var result = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in (seen ?? Enumerable.Empty<string>()).Concat(shown))
        {
            var key = k.Trim();
            if (key.Length > 0 && set.Add(key))
            {
                result.Add(key);
            }
        }

        return result;
    }
}
