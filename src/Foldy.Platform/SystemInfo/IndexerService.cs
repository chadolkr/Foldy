using System.Diagnostics;
using System.ServiceProcess;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// 색인 서비스(FoldyIndexer, foldy-index.exe --service) 설치·제거. SYSTEM 권한 서비스라 항상 UAC를 거친다.
/// 설치는 사용자가 명령을 골랐을 때만 한다 (MSI가 나오면 설치 프로그램이 맡는다, 설계서 10.3).
/// </summary>
public static class IndexerService
{
    public const string Name = "FoldyIndexer";

    public static bool IsInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 서비스 exe가 관리자만 바꿀 수 있는 곳(Program Files, Windows)에 있는지. SYSTEM 서비스의 exe가 사용자 폴더
    /// (다운로드, 바탕화면, 압축 푼 폴더)에 있으면 아무 프로그램이나 그 파일을 바꿔치기해 다음 시작 때 SYSTEM 권한을 얻는다.
    /// </summary>
    public static bool IsProtectedLocation(string exePath, IEnumerable<string?>? protectedRoots = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        protectedRoots ??=
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ];
        string full;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false; // 네트워크 경로
        }

        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && !full.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>관리자 PowerShell 한 번으로 등록·설명·시작 (자동 시작, 1.17.2부터 지연 없이: 켜자마자 빠른 찾기가 색인을 쓰게). exe가 보호된 곳에 없으면 거부한다.</summary>
    public static ProcessStartInfo BuildInstall(string exePath)
    {
        if (!IsProtectedLocation(exePath))
        {
            throw new InvalidOperationException("색인 서비스는 설치본(Program Files)에서만 설치할 수 있어요. 압축본이나 사용자 폴더의 exe를 SYSTEM 권한으로 돌리면 다른 프로그램이 바꿔치기할 수 있어요.");
        }

        var bin = $"\\\"{exePath}\\\" --service";
        var script = string.Join("; ",
            $"sc.exe create {Name} binPath= \"{bin}\" start= auto DisplayName= \"Foldy Indexer\"",
            $"sc.exe description {Name} \"Foldy Indexer by David CHA - 파일 이름 즉시 검색을 위한 디스크 색인\"",
            $"sc.exe failure {Name} reset= 86400 actions= restart/60000/restart/60000//",
            $"sc.exe start {Name}");
        return Elevated(script);
    }

    public static ProcessStartInfo BuildUninstall() =>
        Elevated($"sc.exe stop {Name}; Start-Sleep -Seconds 1; sc.exe delete {Name}");

    private static ProcessStartInfo Elevated(string script) => new(
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
        $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
    };
}
