using System.Runtime.InteropServices;

namespace Foldy.Platform.SystemInfo;

/// <summary>파일을 쓰고 있는 프로그램 하나.</summary>
/// <param name="Name">사람이 알아보는 이름 (예: "Microsoft Excel").</param>
/// <param name="ProcessId">프로세스 번호.</param>
public sealed record FileLocker(string Name, int ProcessId);

/// <summary>
/// "누가 이 파일을 쓰고 있나요?" (PowerToys File Locksmith처럼, 2026-11-21 사용자 승인).
/// Windows Restart Manager에 묻기만 한다: 프로그램을 강제로 끄거나 핸들을 끊지 않는다(쓰던 파일이 깨질 수 있다).
/// 폴더는 안의 파일을 몇 개까지만 물어본다(너무 많으면 느려서).
/// </summary>
public static partial class FileLockers
{
    /// <summary>이름을 모르는 프로그램의 표시 이름 (앱이 화면 언어로 바꿔 끼운다).</summary>
    public static Func<uint, string>? ProgramLabel { get; set; }

    private static string ProgramName(uint pid) => ProgramLabel?.Invoke(pid) ?? $"프로그램 {pid}";

    /// <summary>한 번에 물어보는 파일 수 상한 (폴더 안을 훑을 때).</summary>
    public const int MaxFiles = 2000;

    public static IReadOnlyList<FileLocker> WhoUses(IEnumerable<string> paths)
    {
        var files = Expand(paths).Take(MaxFiles).ToArray();
        if (files.Length == 0)
        {
            return [];
        }

        var key = new char[CCH_RM_SESSION_KEY + 1];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return [];
        }

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
            {
                return [];
            }

            uint needed = 0, count = 0, reasons = 0;
            var r = RmGetList(session, out needed, ref count, null, ref reasons);
            if (r != ERROR_MORE_DATA || needed == 0)
            {
                return [];
            }

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
            {
                return [];
            }

            return [.. infos.Take((int)count)
                .Select(i => new FileLocker(i.strAppName is { Length: > 0 } n ? n : ProgramName(i.Process.dwProcessId), (int)i.Process.dwProcessId))
                .DistinctBy(l => l.ProcessId)];
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    /// <summary>사람에게 보여 줄 한 줄 (없으면 null). 예: "Microsoft Excel이(가) 쓰고 있어요. 그 프로그램을 닫고 다시 해 주세요."</summary>
    public static string? Describe(IReadOnlyList<FileLocker> lockers, Func<string, string>? sentence = null, Func<string, int, string>? more = null)
    {
        if (lockers.Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", lockers.Take(3).Select(l => l.Name));
        if (lockers.Count > 3)
        {
            names = more?.Invoke(names, lockers.Count - 3) ?? $"{names} 외 {lockers.Count - 3}개";
        }

        return sentence?.Invoke(names) ?? $"{names}이(가) 쓰고 있어요. 그 프로그램을 닫고 다시 해 주세요.";
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                yield return p;
            }
            else if (Directory.Exists(p))
            {
                IEnumerable<string> inner;
                try
                {
                    inner = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var f in inner)
                {
                    yield return f;
                }
            }
        }
    }

    private const int CCH_RM_SESSION_KEY = 32;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

#pragma warning disable SYSLIB1054 // 배열 안 문자열 구조체: DllImport 마샬링을 쓴다
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char[] strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
#pragma warning restore SYSLIB1054
}
