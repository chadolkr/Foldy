namespace Foldy.Presentation.Navigation;

/// <summary>경로 정규화·브레드크럼 분해. 디스크에 접근하지 않는다 (UI 스레드에서 안전).</summary>
public static class PathUtil
{
    /// <summary>사용자가 입력한 경로를 정규화한다. 상대 경로나 형식이 틀리면 null.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var p = input.Trim().Trim('"');
        p = Environment.ExpandEnvironmentVariables(p);
        if (p.Length == 2 && p[1] == ':' && char.IsAsciiLetter(p[0]))
        {
            p += "\\";
        }

        var body = p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
        if (body.IndexOfAny(['<', '>', '|', '"', '*', '?']) >= 0 || (body.Length > 2 && body.IndexOf(':', 2) >= 0))
        {
            return null;
        }

        if (!System.IO.Path.IsPathFullyQualified(p))
        {
            return null;
        }

        try
        {
            p = System.IO.Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // 루트("C:\", "\\server\share\")가 아니면 끝 구분자 제거
        var root = System.IO.Path.GetPathRoot(p) ?? "";
        if (p.Length > root.Length)
        {
            p = p.TrimEnd('\\');
        }
        else if (p.Length == 3 && p[1] == ':')
        {
            p = char.ToUpperInvariant(p[0]) + p[1..];
        }

        return p;
    }

    public static string? Parent(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (root is null || string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        return parent is null ? null : Normalize(parent);
    }

    public static string DisplayName(string path)
    {
        var root = System.IO.Path.GetPathRoot(path) ?? path;
        if (string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd('\\');
        }

        return System.IO.Path.GetFileName(path.TrimEnd('\\'));
    }

    /// <summary>브레드크럼 조각: (표시 이름, 그 위치의 전체 경로).</summary>
    public static IReadOnlyList<(string Name, string Path)> Segments(string path)
    {
        var list = new List<(string, string)>();
        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return list;
        }

        list.Add((root.TrimEnd('\\'), Normalize(root) ?? root));
        var rest = path[root.Length..].Trim('\\');
        if (rest.Length == 0)
        {
            return list;
        }

        var acc = root.TrimEnd('\\');
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += "\\" + part;
            list.Add((part, acc));
        }

        return list;
    }
}
