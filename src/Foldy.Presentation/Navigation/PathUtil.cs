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
        if (IsSftp(p))
        {
            return NormalizeSftp(p);
        }

        if (IsGDrive(p))
        {
            return NormalizeGDrive(p);
        }

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

    public const string SftpScheme = "sftp://";

    public static bool IsSftp(string? path) => path?.StartsWith(SftpScheme, StringComparison.OrdinalIgnoreCase) == true;

    public const string GDriveScheme = "gdrive://";

    /// <summary>Google Drive 경로 (gdrive://계정/폴더).</summary>
    public static bool IsGDrive(string? path) => path?.StartsWith(GDriveScheme, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>로컬 파일 시스템이 아닌 URI 경로(SFTP, Google Drive). 셸·Git·폴더 크기·템플릿 같은 로컬 기능을 끈다.</summary>
    public static bool IsRemoteUri(string? path) => IsSftp(path) || IsGDrive(path);

    /// <summary>gdrive://Me@Gmail.com//a/ → gdrive://me@gmail.com/a (계정은 소문자).</summary>
    private static string? NormalizeGDrive(string p)
    {
        var rest = p[GDriveScheme.Length..];
        var slash = rest.IndexOf('/');
        var account = (slash < 0 ? rest : rest[..slash]).Trim().ToLowerInvariant();
        if (account.Length == 0 || account.IndexOfAny(['\\', ':', '?', '#', ' ']) >= 0)
        {
            return null;
        }

        var parts = slash < 0 ? [] : rest[slash..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(x => x is "." or "..") ? null : GDriveScheme + account + "/" + string.Join('/', parts);
    }

    /// <summary>sftp://user@host:port/a/b → 경로 부분의 중복 /와 끝 / 정리 (루트는 /).</summary>
    private static string? NormalizeSftp(string p)
    {
        var rest = p[SftpScheme.Length..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        if (authority.Length == 0)
        {
            return null;
        }

        var path = slash < 0 ? "/" : rest[slash..];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return SftpScheme + authority + "/" + string.Join('/', parts);
    }

    private static (string Root, string[] Parts) SplitSftp(string p)
    {
        var scheme = IsGDrive(p) ? GDriveScheme : SftpScheme;
        var rest = p[scheme.Length..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var parts = slash < 0 ? [] : rest[slash..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (scheme + authority + "/", parts);
    }

    public static string? Parent(string path)
    {
        if (IsRemoteUri(path))
        {
            var (sroot, parts) = SplitSftp(path);
            return parts.Length == 0 ? null : sroot + string.Join('/', parts[..^1]);
        }

        var root = System.IO.Path.GetPathRoot(path);
        if (root is null || string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        return parent is null ? null : Normalize(parent);
    }

    /// <summary>Google Drive 루트(내 드라이브)의 표시 이름.</summary>
    public const string GDriveRootName = "내 드라이브";

    public static string DisplayName(string path)
    {
        if (IsGDrive(path))
        {
            var (_, gparts) = SplitSftp(path);
            return gparts.Length == 0 ? GDriveRootName : gparts[^1];
        }

        if (IsSftp(path))
        {
            var (sroot, parts) = SplitSftp(path);
            return parts.Length == 0 ? sroot[SftpScheme.Length..].TrimEnd('/') : parts[^1];
        }

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
        if (IsRemoteUri(path))
        {
            var (sroot, parts) = SplitSftp(path);
            var authority = sroot[(IsGDrive(path) ? GDriveScheme : SftpScheme).Length..].TrimEnd('/');
            list.Add((IsGDrive(path) ? $"{GDriveRootName} ({authority})" : authority, sroot));
            var sacc = sroot.TrimEnd('/');
            foreach (var part in parts)
            {
                sacc += "/" + part;
                list.Add((part, sacc));
            }

            return list;
        }

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
