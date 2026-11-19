using System.Text.RegularExpressions;

namespace Foldy.Reporting;

/// <summary>
/// 공개 이슈로 나가기 전에 개인 정보를 지운다.
/// 규칙: 사용자 이름·컴퓨터 이름은 자리표시자로, 경로는 지우고 Foldy 소스·실행 파일 이름(.cs .xaml .rs .dll .exe)만 남긴다
/// (사용자 문서 이름은 확장자만: &lt;file&gt;.docx). 주소(URL)·IP·user@서버·서버 쪽 경로(/home/…)·사내 호스트 이름도 지운다.
/// 공개 이슈에 회사 정보(프록시, 서버, 공유 이름)가 남으면 안 된다.
/// 릴레이도 같은 규칙으로 한 번 더 지운다 (tools/crash-relay/src/scrub.ts).
/// </summary>
public sealed partial class ReportScrubber
{
    private readonly string[] _userNames;
    private readonly string _machineName;

    public ReportScrubber(string userName, string machineName, string? userDomainName = null)
    {
        _userNames = new[] { userName, userDomainName is null ? "" : $"{userDomainName}\\{userName}" }
            .Where(s => s.Length >= 2)
            .OrderByDescending(s => s.Length)
            .ToArray();
        _machineName = machineName;
    }

    public static ReportScrubber ForCurrentUser { get; } =
        new(Environment.UserName, Environment.MachineName, Environment.UserDomainName);

    public string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        // 주소를 경로보다 먼저 (file:///C:/… 도 통째로)
        var s = Url().Replace(input, "<url>");
        s = QuotedPath().Replace(s, m => m.Groups[1].Value + KeepFileName(m.Groups[2].Value) + m.Groups[1].Value);
        s = UncPath().Replace(s, m => KeepFileName(m.Value));
        s = DrivePath().Replace(s, m => KeepFileName(m.Value));
        s = Email().Replace(s, "<email>");
        s = Account().Replace(s, "<account>");
        s = IPv4().Replace(s, "<ip>");
        s = IPv6().Replace(s, "<ip>");
        s = PrivateHost().Replace(s, "<host>");
        s = PosixPath().Replace(s, "<path>");
        // 경로 밖에 남은 이름 (예: "사용자 kim의 권한이 없습니다"). 긴 이름부터 (컴퓨터 이름에 사용자 이름이 들어 있곤 하다).
        foreach (var (name, placeholder) in Identifiers)
            s = Regex.Replace(s, NamePattern(name), placeholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return s;
    }

    private IEnumerable<(string Name, string Placeholder)> Identifiers =>
        _userNames.Select(n => (n, "<user>"))
            .Append((_machineName, "<machine>"))
            .Where(p => p.Item1.Length >= 2)
            .OrderByDescending(p => p.Item1.Length);

    /// <summary>
    /// 영문 이름은 영숫자 경계에서만 ("kim"이 "kimchi"를 지우지 않게). 한글 조사("kim의")는 경계로 본다.
    /// 영문이 아닌 이름은 경계 없이 (한글 이름 뒤에 조사가 바로 붙는다).
    /// </summary>
    private static string NamePattern(string name) =>
        name.All(char.IsAscii)
            ? $@"(?<![A-Za-z0-9_-]){Regex.Escape(name)}(?![A-Za-z0-9_-])"
            : Regex.Escape(name);

    private static string KeepFileName(string path)
    {
        if (path.StartsWith("<path>", StringComparison.Ordinal)) return path;
        var trimmed = path.TrimEnd('\\', '/', '.', ',', ';', ')', ']');
        var tail = path[trimmed.Length..];
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        if (!HasExtension().IsMatch(name)) return $"<path>{tail}";
        // Foldy 소스·실행 파일 이름은 디버깅에 필요. 그 밖의 이름(사용자 문서)은 확장자만 남긴다.
        var ext = name[name.LastIndexOf('.')..];
        return KeptExtensions.Contains(ext) ? $"<path>\\{name}{tail}" : $"<path>\\<file>{ext}{tail}";
    }

    // .NET 예외 메시지는 경로를 '...'나 "..."로 감싼다 → 공백이 있어도 통째로
    [GeneratedRegex("""(['"])((?:[A-Za-z]:[\\/]|\\\\)[^'"\r\n]*)\1""")]
    private static partial Regex QuotedPath();

    // 드라이브 문자 뒤에는 ':'가 올 수 없으므로 스택의 ":line 42"는 남는다
    [GeneratedRegex("""\b[A-Za-z]:[\\/][^\s"'<>|*?:\r\n]*""")]
    private static partial Regex DrivePath();

    [GeneratedRegex("""\\\\[^\s\\"'<>|:]+\\[^\s"'<>|*?:\r\n]*""")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex HasExtension();

    private static readonly HashSet<string> KeptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".rs", ".dll", ".exe" };

    // http(s)·ftp·sftp·ssh·file·smb 주소 (프록시 주소 등)
    [GeneratedRegex(@"\b(?:https?|s?ftp|ssh|file|smb|ldaps?|wss?)://[^\s""'<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    // 메일 주소가 아닌 user@서버 (sftp 계정 등). 메일 주소는 먼저 <email>로 바뀐다.
    [GeneratedRegex(@"(?<![\w.%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9][A-Za-z0-9.-]*")]
    private static partial Regex Account();

    // 10.1.2.3, 10.1.2.3:8080. 버전 번호("Version=10.0.0.0", "Foldy 0.3.18.0", "v1.0.0.0")와 더 긴 숫자열은 건드리지 않는다.
    // 첫 자리가 0인 주소(0.x.x.x)는 실제로 쓰이지 않으므로 Foldy 1.0 전 버전 번호로 본다.
    [GeneratedRegex(@"(?<![\w.=])(?<!Foldy )(?<![vV])(?!0\.)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?::\d{1,5})?(?![\w.])")]
    private static partial Regex IPv4();

    // fe80::1, 2001:db8::1 (시각 "10:49:42"는 콜론이 둘뿐이라 남는다. 숫자가 하나는 있어야 한다: "a::b" 같은 코드 이름 보호)
    [GeneratedRegex(@"(?<![\w:])(?=[0-9A-Fa-f:]*\d)(?=[0-9A-Fa-f:]*(?:::|(?:[0-9A-Fa-f]*:){3}))[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![\w:])")]
    private static partial Regex IPv6();

    // 사내 도메인 이름 (fileserver.corp.local 등)
    [GeneratedRegex(@"\b[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*\.(?:local|lan|corp|internal|intranet|intra|localdomain|home\.arpa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateHost();

    // 서버 쪽 경로 (/home/kim/…, /var/…). 단어·숫자 바로 뒤의 '/'(날짜 11/19, 타입 A/B)는 경로로 보지 않는다.
    [GeneratedRegex(@"(?<![\w.:/<>\\])/(?:[^\s/""'<>|]+/)+[^\s""'<>|]*")]
    private static partial Regex PosixPath();
}
