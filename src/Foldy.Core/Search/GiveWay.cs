using System.Runtime.InteropServices;

namespace Foldy.Core.Search;

/// <summary>
/// 다른 프로그램에 비켜 주며 읽기 (Windows 스레드 백그라운드 모드: 디스크 순서를 맨 뒤로, CPU·메모리 순서는 원래대로).
/// 아무도 안 쓰면 빠르고, 누가 같은 드라이브를 쓰면 저절로 물러난다
/// (2026-11-24 SD 카드 실측: 혼자일 때 4.5초, 다른 프로그램이 그 드라이브를 계속 읽을 때 195초, 그 프로그램의 읽기는 3.8ms 그대로).
/// CPU 순서까지 맨 뒤로 두면 PC가 바쁠 때 멈추다시피 해서(폴더 하나에 0.2초) 되돌려 둔다. 켜는 순간에는 CPU 순서도 잠깐 낮으므로 폴더마다 켰다 끄지 말고 한 번에 쓴다.
/// 스레드 하나에만 걸리고 끝나면 풀린다. 네트워크·클라우드 드라이브에서는 별 효과가 없다.
/// </summary>
internal static class GiveWay
{
    private const int BackgroundBegin = 0x10000;
    private const int BackgroundEnd = 0x20000;
    private const int ThreadMemoryPriority = 0;
    private const int MemoryPriorityNormal = 5;

    public static void Run(Action read)
    {
        var on = false;
        if (OperatingSystem.IsWindows())
        {
            var cpu = GetThreadPriority(GetCurrentThread());
            on = SetThreadPriority(GetCurrentThread(), BackgroundBegin);
            if (on)
            {
                SetThreadPriority(GetCurrentThread(), cpu);
                var memory = MemoryPriorityNormal; // 읽어 둔 폴더를 Windows가 먼저 잊지 않게 (다시 모을 때 12초 → 3초)
                SetThreadInformation(GetCurrentThread(), ThreadMemoryPriority, ref memory, sizeof(int));
            }
        }

        try
        {
            read();
        }
        finally
        {
            if (on)
            {
                SetThreadPriority(GetCurrentThread(), BackgroundEnd);
            }
        }
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadInformation(nint thread, int cls, ref int value, int size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetThreadPriority(nint thread);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
