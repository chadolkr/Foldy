using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Serilog;

namespace Foldy.App.Diagnostics;

/// <summary>
/// Foldy는 관리자 권한으로 돌면 안 된다. 관리자 토큰에서는 사용자의 매핑 드라이브·리다이렉터 드라이브
/// (예: 회사 보안 리다이렉터 드라이브)가 비어 보이고, 끌어 놓기도 막힌다.
/// 관리자로 시작됐으면 탐색기를 통해 일반 권한으로 다시 띄우고 끝낸다.
/// 탐색기까지 관리자인 PC(UAC 꺼짐)에서 무한 반복하지 않게 30초 안에 한 번만 시도한다.
/// </summary>
internal static class Deelevate
{
    public static bool RelaunchIfElevated(string[] args)
    {
        if (args.Contains("--allow-admin", StringComparer.OrdinalIgnoreCase) || !IsElevated())
        {
            return false;
        }

        var marker = Path.Combine(Path.GetTempPath(), "Foldy.deelevate");
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromSeconds(30))
            {
                return false; // 방금 시도했는데 또 관리자 → 탐색기도 관리자. 그대로 실행
            }

            File.WriteAllText(marker, "");
            var exe = Environment.ProcessPath!;
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            return true;
        }
#pragma warning disable CA1031 // 실패하면 관리자 그대로 실행
        catch (Exception ex)
        {
            Log.Warning(ex, "일반 권한으로 다시 띄우지 못함");
            return false;
        }
#pragma warning restore CA1031
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
