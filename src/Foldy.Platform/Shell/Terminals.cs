using System.Diagnostics;
using Foldy.Platform.SystemInfo;

namespace Foldy.Platform.Shell;

public enum ShellKind { Pwsh, WindowsPowerShell, Cmd }

/// <summary>
/// 현재 폴더에서 터미널 열기 (DESIGN.md SFTP/SSH와 터미널 아이콘).
/// <list type="bullet">
/// <item>Windows Terminal이 있으면 wt -d, 없으면 기본 콘솔. PowerShell 7(pwsh) 우선.</item>
/// <item>관리자 실행은 작업 폴더가 무시되므로 경로를 명령으로 넘긴다. 매핑 드라이브는 UNC로 바꾼다.</item>
/// <item>CMD는 UNC로 cd를 못 하므로 pushd를 쓴다.</item>
/// <item>관리자는 항상 UAC를 거치고 자격 증명을 저장하지 않는다.</item>
/// </list>
/// </summary>
public static class Terminals
{
    public static string? WindowsTerminal()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        return File.Exists(p) ? p : FindOnPath("wt.exe");
    }

    public static string? Pwsh()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p = Path.Combine(pf, @"PowerShell\7\pwsh.exe");
        return File.Exists(p) ? p : FindOnPath("pwsh.exe");
    }

    public static string WindowsPowerShell() => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static string Cmd() => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static string? GitBash()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var p = Path.Combine(pf, @"Git\git-bash.exe");
        return File.Exists(p) ? p : null;
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// 따옴표 안에서 깨지지 않는 폴더 경로. 끝이 \면("C:\") 닫는 따옴표가 \"로 이스케이프되어
    /// 인자가 엉키므로 "C:\."처럼 끝을 바꾼다 (모든 셸·wt에서 같은 폴더).
    /// </summary>
    internal static string SafeDir(string folder) =>
        folder.EndsWith('\\') ? folder + "." : folder;

    /// <summary>wt 명령줄에서 ;는 새 탭 구분자라 \;로 바꾼다.</summary>
    private static string WtEscape(string s) => s.Replace(";", "\\;", StringComparison.Ordinal);

    /// <summary>실행할 프로세스 정보를 만든다 (테스트 가능하게 실행과 분리).</summary>
    public static ProcessStartInfo Build(string folder, ShellKind shell, bool elevated, bool useWindowsTerminal, string? wtProfile = null)
    {
        var isUnc = folder.StartsWith(@"\\", StringComparison.Ordinal);
        var target = SafeDir(elevated ? Drives.ToUnc(folder) : folder);
        isUnc |= target.StartsWith(@"\\", StringComparison.Ordinal);
        var ps = $"-NoExit -Command \"Set-Location -LiteralPath '{target.Replace("'", "''", StringComparison.Ordinal)}'\"";

        var (exe, args) = shell switch
        {
            ShellKind.Cmd => (Cmd(), isUnc || elevated ? $"/k pushd \"{target}\"" : ""),
            ShellKind.WindowsPowerShell => (WindowsPowerShell(), ps),
            _ => (Pwsh() ?? WindowsPowerShell(), ps),
        };

        // 관리자가 아니고 로컬 폴더면 작업 폴더만 지정해도 충분 (인자 없이 깔끔하게)
        if (!elevated && !isUnc && shell != ShellKind.Cmd)
        {
            args = "-NoExit";
        }

        // 관리자 실행은 wt를 거치지 않는다: wt.exe는 앱 실행 별칭(WindowsApps)이라 runas로 띄우면 실패한다.
        var wt = useWindowsTerminal && !elevated ? WindowsTerminal() : null;
        ProcessStartInfo psi;
        if (wt is not null)
        {
            var profile = string.IsNullOrEmpty(wtProfile) ? "" : $"-p \"{wtProfile}\" ";
            var dir = isUnc ? "." : $"\"{WtEscape(target)}\"";
            // 셸은 실행 파일 이름으로 넘긴다 (wt가 PATH에서 찾는다. WindowsApps 별칭 전체 경로는 wt에서 실패할 수 있다)
            psi = new ProcessStartInfo(wt, $"{profile}-d {dir} {Path.GetFileName(exe)} {WtEscape(args)}".TrimEnd());
        }
        else
        {
            psi = new ProcessStartInfo(exe, args);
        }

        psi.UseShellExecute = true;
        if (!isUnc)
        {
            psi.WorkingDirectory = target;
        }

        if (elevated)
        {
            psi.Verb = "runas";
        }

        return psi;
    }

    /// <summary>
    /// 터미널을 연다. 실패하면(별칭을 관리자로 못 띄우는 등) Windows PowerShell → 명령 프롬프트 순으로 다시 시도한다.
    /// 사용자가 UAC를 취소한 경우(1223)는 그대로 끝낸다.
    /// </summary>
    public static void Open(string folder, ShellKind shell, bool elevated, bool useWindowsTerminal, string? wtProfile = null)
    {
        var attempts = new List<ProcessStartInfo> { Build(folder, shell, elevated, useWindowsTerminal, wtProfile) };
        if (useWindowsTerminal && !elevated)
        {
            attempts.Add(Build(folder, shell, elevated, useWindowsTerminal: false));
        }

        if (shell == ShellKind.Pwsh)
        {
            attempts.Add(Build(folder, ShellKind.WindowsPowerShell, elevated, useWindowsTerminal: false));
        }

        System.ComponentModel.Win32Exception? last = null;
        foreach (var psi in attempts)
        {
            try
            {
                using var _ = Process.Start(psi);
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode != 1223)
            {
                last = ex;
            }
        }

        throw last!;
    }
}
