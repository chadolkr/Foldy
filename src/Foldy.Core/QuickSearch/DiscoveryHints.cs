namespace Foldy.Core.QuickSearch;

/// <summary>처음 쓰는 사람에게 알맞은 순간에 한 번만 알려 줄 숨은 기능.</summary>
public enum DiscoveryRule
{
    None,

    /// <summary>두 창에 이름이 같은 폴더 → 두 창 비교·맞추기.</summary>
    Compare,

    /// <summary>node_modules·bin/obj 같은 다시 만들 수 있는 폴더 → 지워도 되는 폴더 찾기.</summary>
    Rebuildable,

    /// <summary>사진이 대부분인 폴더 → 이미지만 보기.</summary>
    Images,
}

/// <summary>
/// 기능 찾기 한 줄 제안 (사용자 2026-11-22 "좋은 기능이 있어도 처음 사용하는 사람은 기능이 어디 있는지 모른다").
/// 지금 보는 폴더(이미 불러온 목록)만 보고 고른다. 디스크를 더 읽지 않는다. 같은 제안은 평생 한 번만 띄운다(호출하는 쪽이 기억).
/// </summary>
public static class DiscoveryHints
{
    /// <summary>"이미지만 보기"를 권하는 사진 수.</summary>
    public const int MinImages = 30;

    /// <summary>사진이 파일 중 이 비율 이상일 때만.</summary>
    public const double ImageShare = 0.7;

    /// <summary>
    /// 지금 폴더에 맞는 제안 하나 (없으면 None). 순서: 두 창 비교 → 지워도 되는 폴더 → 이미지만 보기.
    /// <paramref name="count"/>는 이미지 수(Images일 때만).
    /// </summary>
    public static DiscoveryRule Pick(string folder, IReadOnlyCollection<(string Name, bool IsFolder)> items, string? otherFolder,
        bool imagesOn, Func<string, bool> isImage, out int count)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isImage);
        count = 0;
        if (!IsLocal(folder))
        {
            return DiscoveryRule.None;
        }

        if (otherFolder is not null && IsLocal(otherFolder) && SameName(folder, otherFolder))
        {
            return DiscoveryRule.Compare;
        }

        var folders = new HashSet<string>(items.Where(i => i.IsFolder).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        if (folders.Contains("node_modules") || folders.Contains("__history") || (folders.Contains("bin") && folders.Contains("obj")))
        {
            return DiscoveryRule.Rebuildable;
        }

        if (!imagesOn)
        {
            var files = 0;
            var images = 0;
            foreach (var i in items)
            {
                if (i.IsFolder)
                {
                    continue;
                }

                files++;
                if (isImage(i.Name))
                {
                    images++;
                }
            }

            if (images >= MinImages && images >= ImageShare * files)
            {
                count = images;
                return DiscoveryRule.Images;
            }
        }

        return DiscoveryRule.None;
    }

    private static bool IsLocal(string p) => p.Length >= 3 && p[1] == ':' && char.IsAsciiLetter(p[0]);

    /// <summary>다른 곳에 있는 이름이 같은 폴더 (예: D:\사진 과 E:\백업\사진). 드라이브 맨 위는 빼고.</summary>
    private static bool SameName(string a, string b)
    {
        var x = a.TrimEnd('\\');
        var y = b.TrimEnd('\\');
        if (x.Length <= 2 || y.Length <= 2 || string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(x), Path.GetFileName(y), StringComparison.OrdinalIgnoreCase);
    }
}
