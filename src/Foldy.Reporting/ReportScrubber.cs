using System.Text.RegularExpressions;

namespace Foldy.Reporting;

/// <summary>
/// 공개 이슈로 나가기 전에 개인 정보를 지운다.
/// 규칙: 사용자 이름·컴퓨터 이름은 자리표시자로, 경로는 확장자 있는 파일 이름만, 메일 주소는 삭제.
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
        var s = QuotedPath().Replace(input, m => m.Groups[1].Value + KeepFileName(m.Groups[2].Value) + m.Groups[1].Value);
        s = UncPath().Replace(s, m => KeepFileName(m.Value));
        s = DrivePath().Replace(s, m => KeepFileName(m.Value));
        s = Email().Replace(s, "<email>");
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
        return HasExtension().IsMatch(name) ? $"<path>\\{name}{tail}" : $"<path>{tail}";
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
}
