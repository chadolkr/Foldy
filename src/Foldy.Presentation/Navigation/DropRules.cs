namespace Foldy.Presentation.Navigation;

/// <summary>파일을 끌어 놓을 때의 규칙. 디스크에 접근하지 않는다.</summary>
public static class DropRules
{
    /// <summary>
    /// 놓으려는 파일이 모두 이미 그 폴더에 있으면 true (같은 폴더를 연 반대쪽 창에 놓기, 탐색기에서 같은 폴더로 끌어오기).
    /// 이때는 아무것도 하지 않고, 끌어 온 창이 다른 창이면 쉬운 말로 이유를 알려 준다.
    /// </summary>
    public static bool AllAlreadyIn(IReadOnlyList<string>? files, string? sourceFolder, string target)
    {
        if (Same(sourceFolder, target))
        {
            return true;
        }

        return files is { Count: > 0 } && files.All(f => PathUtil.Parent(f) is { } parent && Same(parent, target));
    }

    /// <summary>
    /// 놓을 곳이 끌어 온 폴더 자신이거나 그 안쪽이면 true (폴더를 자기 안에 복사·이동하면 끝나지 않는다).
    /// </summary>
    public static bool IntoItself(IReadOnlyList<string>? files, string target)
    {
        if (files is not { Count: > 0 })
        {
            return false;
        }

        var t = Trim(target);
        return files.Any(f =>
        {
            var folder = Trim(f);
            return folder.Length > 0 && (string.Equals(t, folder, StringComparison.OrdinalIgnoreCase)
                || (t.Length > folder.Length && t.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && t[folder.Length] is '\\' or '/'));
        });
    }

    /// <summary>
    /// 놓을 곳: 커서 아래 줄이 폴더이고 끌고 있는 것 중 하나가 아니면 그 폴더, 아니면 지금 연 폴더.
    /// 같은 창 안에서 파일을 하위 폴더 위에 놓아 옮길 수 있게 한다 (탐색기와 같음).
    /// 끌기 시작한 바로 그 폴더 위(자기 자신)는 놓을 곳으로 보지 않는다.
    /// </summary>
    public static string TargetFor(IReadOnlyList<string>? files, string currentFolder, string? folderUnderCursor)
    {
        if (string.IsNullOrEmpty(folderUnderCursor))
        {
            return currentFolder;
        }

        if (files is { Count: > 0 } && files.Any(f => Same(f, folderUnderCursor)))
        {
            return currentFolder;
        }

        return folderUnderCursor;
    }

    private static string Trim(string p) => (PathUtil.Normalize(p) ?? p).TrimEnd('\\', '/');

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);
}
