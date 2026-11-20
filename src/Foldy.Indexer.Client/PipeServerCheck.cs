using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Foldy.Indexer.Client;

/// <summary>
/// 연결한 파이프의 서버가 믿을 만한지 확인한다 (보안 리뷰 L3).
/// 서비스보다 먼저 다른 사용자가 같은 이름의 파이프를 만들어 두면 검색 결과를 꾸며 보낼 수 있으므로,
/// 서버 프로세스가 설치된 색인 서비스(FoldyIndexer)이거나, SYSTEM이거나, 나와 같은 사용자(개발용 콘솔 실행)일 때만 쓴다.
/// </summary>
/// <remarks>
/// 보통 권한(중간 무결성)의 Foldy는 SYSTEM 프로세스의 토큰을 열 수 없다(OpenProcessToken 거부). 그래서 계정만 보면
/// 진짜 서비스도 "믿을 수 없음"이 되어 색인 검색이 꺼졌다 (0.9.3까지의 회귀, CI는 관리자 권한이라 못 잡음).
/// 먼저 서비스 관리자(SCM)에게 FoldyIndexer 서비스의 프로세스 번호를 물어 파이프 상대와 같은지 본다. 서비스 등록은
/// 관리자만 바꿀 수 있으므로 가짜 프로그램이 이 번호를 흉내 낼 수 없다.
/// </remarks>
internal static partial class PipeServerCheck
{
    public static bool IsTrusted(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid) || pid == 0)
        {
            return false;
        }

        if (ServiceProcessId(ServiceName) is { } svc && svc == pid)
        {
            return true;
        }

        using var current = WindowsIdentity.GetCurrent();
        return ServerUser(pid) is { } sid && IsTrustedUser(sid, current.User);
    }

    public const string ServiceName = "FoldyIndexer";

    /// <summary>서비스가 돌고 있으면 그 프로세스 번호 (서비스 관리자에게 묻는다, 보통 권한으로도 된다).</summary>
    internal static uint? ServiceProcessId(string name)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buf = new byte[36]; // SERVICE_STATUS_PROCESS
                if (!QueryServiceStatusEx(svc, ScStatusProcessInfo, buf, buf.Length, out _))
                {
                    return null;
                }

                var state = BitConverter.ToUInt32(buf, 4);
                var pid = BitConverter.ToUInt32(buf, 28);
                return state == ServiceRunning && pid != 0 ? pid : null;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int infoLevel, [Out] byte[] buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    /// <summary>SYSTEM이거나 지금 사용자와 같은 계정이면 참.</summary>
    internal static bool IsTrustedUser(SecurityIdentifier server, SecurityIdentifier? current) =>
        server.IsWellKnown(WellKnownSidType.LocalSystemSid) || (current is not null && server == current);

    private static SecurityIdentifier? ServerUser(uint pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            try
            {
                using var id = new WindowsIdentity(token.DangerousGetHandle());
                return id.User;
            }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
