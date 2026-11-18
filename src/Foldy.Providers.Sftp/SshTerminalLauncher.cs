using System.Text;
using System.Text.RegularExpressions;

namespace Foldy.Providers.Sftp;

/// <summary>외부 SSH 터미널을 열기 위한 정보. 같은 서버·같은 계정·같은 원격 경로.</summary>
public sealed record SshTerminalRequest(
    string Host,
    int Port,
    string User,
    string? RemotePath = null,
    string? KeyFilePath = null,
    bool Sudo = false);

/// <summary>실행할 프로세스. 셸 실행(UseShellExecute)이므로 새 창(Windows Terminal 또는 콘솔)이 뜬다.</summary>
public sealed record TerminalLaunch(string FileName, string Arguments, bool UseShellExecute = true);

/// <summary>
/// SSH 터미널 실행 명령 조립(순수 함수, 설계서 SFTP/SSH 절).
/// <c>wt ssh -t -p 22 [-i key] user@host "cd '/path' &amp;&amp; exec $SHELL -l"</c>.
/// Windows Terminal이 없으면 ssh.exe를 콘솔로 직접 실행한다. 비밀번호는 절대 명령줄에 넣지 않는다(터미널에서 직접 입력).
/// </summary>
public static partial class SshTerminalLauncher
{
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@\\-]*$")]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.:%\-]*$")]
    private static partial Regex HostPattern();

    /// <summary>원격 셸에서 실행할 문자열. 경로는 POSIX 작은따옴표로 감싸 공백·메타문자를 막는다.</summary>
    public static string BuildRemoteCommand(string? remotePath, bool sudo)
    {
        var hasPath = !string.IsNullOrEmpty(remotePath);
        if (!sudo)
        {
            return hasPath
                ? $"cd {QuotePosix(remotePath!)} && exec $SHELL -l"
                : "exec $SHELL -l";
        }

        // sudo -i는 홈으로 이동하므로, 경로가 있으면 로그인 셸 안에서 cd 후 셸을 다시 띄운다.
        // $SHELL은 sudo -i 환경(대상 사용자)에서 펼쳐지도록 안쪽 따옴표에 둔다. 비밀번호는 터미널에서 직접 입력한다.
        return hasPath
            ? "exec sudo -i sh -c " + QuotePosix($"cd {QuotePosix(remotePath!)} && exec \"$SHELL\" -l")
            : "exec sudo -i";
    }

    public static TerminalLaunch Build(SshTerminalRequest request, string? windowsTerminalPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var args = new List<string> { "-t", "-p", request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(request.KeyFilePath))
        {
            args.Add("-i");
            args.Add(request.KeyFilePath);
        }

        args.Add($"{request.User}@{request.Host}");
        args.Add(BuildRemoteCommand(request.RemotePath, request.Sudo));

        if (windowsTerminalPath is null)
        {
            return new TerminalLaunch("ssh.exe", JoinWindowsArgs(args));
        }

        // wt는 ';'를 자체 명령 구분자로 쓰므로 인수 안의 ';'는 '\;'로 이스케이프한다.
        var wtArgs = new List<string> { "ssh" };
        wtArgs.AddRange(args.Select(a => a.Replace(";", "\\;", StringComparison.Ordinal)));
        return new TerminalLaunch(windowsTerminalPath, JoinWindowsArgs(wtArgs));
    }

    /// <summary>wt.exe 위치. 앱 실행 별칭 폴더 → PATH 순으로 찾는다. 없으면 null.</summary>
    public static string? FindWindowsTerminal(string? localAppData, IEnumerable<string> pathDirs, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(pathDirs);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrEmpty(localAppData))
        {
            var alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe");
            if (fileExists(alias))
            {
                return alias;
            }
        }

        foreach (var dir in pathDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            var candidate = Path.Combine(dir.Trim('"'), "wt.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>실제 환경에서 wt.exe를 찾는다.</summary>
    public static string? FindWindowsTerminal() => FindWindowsTerminal(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator),
        File.Exists);

    /// <summary>POSIX 셸 작은따옴표 인용: ' → '\'' .</summary>
    public static string QuotePosix(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>CommandLineToArgvW 규칙에 맞는 Windows 명령줄 인수 인용.</summary>
    public static string QuoteWindowsArg(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private static string JoinWindowsArgs(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteWindowsArg));

    private static void Validate(SshTerminalRequest r)
    {
        // '-'로 시작하는 값이 ssh 옵션(-oProxyCommand=...)으로 해석되는 것을 막는다.
        if (!UserPattern().IsMatch(r.User))
        {
            throw new ArgumentException($"SSH 사용자 이름이 올바르지 않습니다: {r.User}", nameof(r));
        }

        if (!HostPattern().IsMatch(r.Host))
        {
            throw new ArgumentException($"SSH 호스트 이름이 올바르지 않습니다: {r.Host}", nameof(r));
        }

        if (r.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"SSH 포트가 올바르지 않습니다: {r.Port}", nameof(r));
        }
    }
}
