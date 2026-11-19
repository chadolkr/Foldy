using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Foldy.Indexer.Client;

/// <summary>
/// 연결한 파이프의 서버가 믿을 만한지 확인한다 (보안 리뷰 L3).
/// 서비스보다 먼저 다른 사용자가 같은 이름의 파이프를 만들어 두면 검색 결과를 꾸며 보낼 수 있으므로,
/// 서버 프로세스가 SYSTEM(설치된 서비스)이거나 나와 같은 사용자(개발용 콘솔 실행)일 때만 쓴다.
/// </summary>
internal static partial class PipeServerCheck
{
    public static bool IsTrusted(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid) || pid == 0)
        {
            return false;
        }

        using var current = WindowsIdentity.GetCurrent();
        return ServerUser(pid) is { } sid && IsTrustedUser(sid, current.User);
    }

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
