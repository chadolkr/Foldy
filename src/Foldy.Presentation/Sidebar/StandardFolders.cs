using Foldy.Core.FileSystem;
using Foldy.Core.Localization;

namespace Foldy.Presentation.Sidebar;

/// <summary>왼쪽(사이드바 "최근 폴더" 위·폴더 트리 맨 위)에 늘 보이는 Windows 기본 폴더와 그 이름·그림.</summary>
public static class StandardFolders
{
    /// <summary>
    /// 이 PC에 있는 기본 폴더(Windows 11 탐색기 홈과 같은 순서). 위치는 Known Folder로 물어서 OneDrive로 옮긴 경우도 맞다.
    /// 없는 폴더(지웠거나 회사 PC에서 막음)는 빼고, 두 폴더가 같은 자리(옛 PC에서 사진=문서 등)면 앞의 것만.
    /// </summary>
    public static IReadOnlyList<StandardFolder> All(Func<KnownFolder, string?>? pathOf = null, Func<string, bool>? exists = null)
    {
        pathOf ??= KnownFolders.PathOf;
        exists ??= Directory.Exists;
        var list = new List<StandardFolder>();
        foreach (var k in KnownFolders.Shown)
        {
            if (pathOf(k) is { Length: > 0 } p && exists(p) && !list.Any(f => string.Equals(f.Path.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new StandardFolder(p, Name(k), Glyph(k)));
            }
        }

        return list;
    }

    public static string Name(KnownFolder k) => k switch
    {
        KnownFolder.Desktop => Loc.T("tree.desktop"),
        KnownFolder.Downloads => Loc.T("tree.downloads"),
        KnownFolder.Documents => Loc.T("tree.documents"),
        KnownFolder.Pictures => Loc.T("tree.pictures"),
        KnownFolder.Videos => Loc.T("tree.videos"),
        _ => k.ToString(),
    };

    /// <summary>폴더마다 다른 그림 (Segoe Fluent Icons): 모니터·아래 화살표·문서·사진·필름.</summary>
    public static string Glyph(KnownFolder k) => k switch
    {
        KnownFolder.Desktop => "",
        KnownFolder.Downloads => "",
        KnownFolder.Documents => "",
        KnownFolder.Pictures => "",
        KnownFolder.Videos => "",
        _ => "",
    };
}
