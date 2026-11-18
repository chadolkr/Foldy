using System.Text;

namespace Foldy.Providers.Sftp;

/// <summary>
/// <c>sftp://user@host:port/path</c> 형태의 위치. 경로는 원격 서버의 POSIX 절대 경로이며 그대로 보존한다(퍼센트 인코딩 없음).
/// 사용자 이름만 퍼센트 인코딩을 허용한다(예: <c>me%40corp</c>).
/// </summary>
public sealed record SftpLocation(string? User, string Host, int Port, string Path)
{
    public const int DefaultPort = 22;
    public const string Scheme = "sftp://";

    /// <summary>연결(큐·세션) 식별 키. 경로는 포함하지 않는다.</summary>
    public string ConnectionKey => $"{User ?? ""}@{Host.ToLowerInvariant()}:{Port}";

    public static bool TryParse(string? text, out SftpLocation location)
    {
        location = null!;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[Scheme.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        string? user = null;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var rawUser = authority[..at];
            authority = authority[(at + 1)..];
            if (rawUser.Length == 0)
            {
                return false;
            }

            try
            {
                user = Uri.UnescapeDataString(rawUser);
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        string host;
        var port = DefaultPort;
        if (authority.StartsWith('['))
        {
            // IPv6 리터럴: [::1] 또는 [::1]:2222
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = authority[1..close];
            var tail = authority[(close + 1)..];
            if (tail.Length > 0 && (tail[0] != ':' || !TryParsePort(tail[1..], out port)))
            {
                return false;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];
                if (!TryParsePort(authority[(colon + 1)..], out port))
                {
                    return false;
                }
            }
            else
            {
                host = authority;
            }
        }

        if (host.Length == 0 || host.StartsWith('-') || host.Any(char.IsWhiteSpace))
        {
            return false;
        }

        location = new SftpLocation(user, host, port, NormalizePath(path));
        return true;
    }

    public static SftpLocation Parse(string text) =>
        TryParse(text, out var l) ? l : throw new FormatException($"SFTP 주소가 올바르지 않습니다: {text}");

    /// <summary>같은 연결에서 경로만 바꾼 위치.</summary>
    public SftpLocation WithPath(string path) => this with { Path = NormalizePath(path) };

    /// <summary>자식 이름을 붙인 위치.</summary>
    public SftpLocation Combine(string name) =>
        WithPath(Path.EndsWith('/') ? Path + name : Path + "/" + name);

    /// <summary>한 단계 위. 루트면 null.</summary>
    public SftpLocation? Parent()
    {
        if (Path == "/")
        {
            return null;
        }

        var i = Path.LastIndexOf('/');
        return WithPath(i <= 0 ? "/" : Path[..i]);
    }

    /// <summary>표준 문자열. 기본 포트(22)는 생략한다.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Scheme);
        if (User is not null)
        {
            sb.Append(Uri.EscapeDataString(User)).Append('@');
        }

        sb.Append(Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host);
        if (Port != DefaultPort)
        {
            sb.Append(':').Append(Port);
        }

        return sb.Append(Path).ToString();
    }

    /// <summary>중복 슬래시와 끝 슬래시를 정리한다. "." / ".." 구성요소는 서버가 해석하도록 건드리지 않는다.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }

    private static bool TryParsePort(string s, out int port) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;
}
