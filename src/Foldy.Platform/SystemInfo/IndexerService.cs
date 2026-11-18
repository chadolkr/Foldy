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

    /// <summary>관리자 PowerShell 한 번으로 등록·설명·시작 (지연된 자동 시작).</summary>
    public static ProcessStartInfo BuildInstall(string exePath)
    {
        var bin = $"\\\"{exePath}\\\" --service";
        var script = string.Join("; ",
            $"sc.exe create {Name} binPath= \"{bin}\" start= delayed-auto DisplayName= \"Foldy Indexer\"",
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
