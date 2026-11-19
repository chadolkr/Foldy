using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 업데이트로 아이콘이 바뀌어도 Windows 아이콘 캐시 때문에 작업 표시줄·바로가기에 옛 아이콘이 남는다.
/// 새 버전으로 처음 실행될 때 한 번 셸에 "연결·아이콘이 바뀌었다"고 알려 캐시를 새로 읽게 한다.
/// </summary>
internal static class IconRefresh
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int ShcneAssocChanged = 0x08000000;

    public static void OnceForVersion(string dataDir, string version)
    {
        try
        {
            var marker = Path.Combine(dataDir, "last-run-version.txt");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version)
            {
                return;
            }

            Directory.CreateDirectory(dataDir);
            File.WriteAllText(marker, version);
            _ = Task.Run(() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero));
            Log.Information("새 버전 {Version} 첫 실행: 아이콘 캐시 새로 고침 요청", version);
        }
#pragma warning disable CA1031 // 아이콘 새로 고침 실패는 무시
        catch (Exception ex)
        {
            Log.Debug(ex, "아이콘 캐시 새로 고침 실패");
        }
#pragma warning restore CA1031
    }
}
