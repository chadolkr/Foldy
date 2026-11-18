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

    /// <summary>실행할 프로세스 정보를 만든다 (테스트 가능하게 실행과 분리).</summary>
    public static ProcessStartInfo Build(string folder, ShellKind shell, bool elevated, bool useWindowsTerminal, string? wtProfile = null)
    {
        var isUnc = folder.StartsWith(@"\\", StringComparison.Ordinal);
        var target = elevated ? Drives.ToUnc(folder) : folder;
        isUnc |= target.StartsWith(@"\\", StringComparison.Ordinal);

        var (exe, args) = shell switch
        {
            ShellKind.Cmd => (Cmd(), isUnc || elevated ? $"/k pushd \"{target}\"" : ""),
            ShellKind.WindowsPowerShell => (WindowsPowerShell(), $"-NoExit -Command \"Set-Location -LiteralPath '{target.Replace("'", "''", StringComparison.Ordinal)}'\""),
            _ => (Pwsh() ?? WindowsPowerShell(), $"-NoExit -Command \"Set-Location -LiteralPath '{target.Replace("'", "''", StringComparison.Ordinal)}'\""),
        };

        // 관리자가 아니고 로컬 폴더면 작업 폴더만 지정해도 충분 (인자 없이 깔끔하게)
        if (!elevated && !isUnc && shell != ShellKind.Cmd)
        {
            args = "-NoExit";
        }

        ProcessStartInfo psi;
        var wt = useWindowsTerminal ? WindowsTerminal() : null;
        if (wt is not null)
        {
            var profile = string.IsNullOrEmpty(wtProfile) ? "" : $"-p \"{wtProfile}\" ";
            var dir = isUnc ? "." : $"\"{target}\"";
            psi = new ProcessStartInfo(wt, $"{profile}-d {dir} \"{exe}\" {args}".TrimEnd());
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

    public static void Open(string folder, ShellKind shell, bool elevated, bool useWindowsTerminal, string? wtProfile = null)
    {
        using var _ = Process.Start(Build(folder, shell, elevated, useWindowsTerminal, wtProfile));
    }
}
