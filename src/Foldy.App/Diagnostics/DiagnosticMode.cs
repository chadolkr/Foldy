using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Foldy.App.Diagnostics;

/// <summary>
/// 진단 모드: 켜면 상세(Debug) 기록을 남기고 24시간 뒤 저절로 꺼진다. 평소에는 Information 이상만.
/// 폴더·드라이브 열기 실패는 진단 모드와 상관없이 원인 파악에 필요한 정보(드라이브 종류, 매핑 UNC,
/// 프로세스 권한·세션, Win32 오류 코드, 걸린 시간)를 남긴다. 드라이브 루트 실패는 오류(Error)로 남겨
/// 동의한 경우 오류 보고서로 자동 신고된다 (경로·사용자 이름은 보고서에서 가려진다).
/// </summary>
internal static class DiagnosticMode
{
    public static readonly LoggingLevelSwitch Level = new(LogEventLevel.Information);

    public static bool IsOn => Level.MinimumLevel <= LogEventLevel.Debug;

    public static void Apply(DateTime? untilUtc)
    {
        var on = untilUtc is { } u && u > DateTime.UtcNow;
        Level.MinimumLevel = on ? LogEventLevel.Debug : LogEventLevel.Information;
        if (on)
        {
            Log.Information("진단 모드 켜짐 (~{Until:u}). {Env}", untilUtc, ProcessContext());
        }
    }

    /// <summary>폴더 열기 결과 (FolderTabViewModel 훅). 실패면 원인 정보를, 성공이면 진단 모드에서만 시간을 남긴다.</summary>
    public static void FolderLoaded(string path, TimeSpan elapsed, int count, Exception? error)
    {
        var isLocalPath = path.Length >= 2 && path[1] == ':';
        var root = isLocalPath ? path[..2] + "\\" : null;
        if (error is null)
        {
            if (root is not null && path.Length <= 3 && count == 0)
            {
                // 드라이브 루트가 비어 보이는 경우 (매핑 드라이브가 이 로그온 세션에 없을 때 등)
                Log.Warning("드라이브 {Root}가 비어 있음 ({Ms}ms). {Drive} {Raw} {Env}", root, (int)elapsed.TotalMilliseconds, DriveContext(root), RawEnumerate(root), ProcessContext());
            }
            else
            {
                Log.Debug("폴더 열기 {Path}: {Count}개, {Ms}ms", path, count, (int)elapsed.TotalMilliseconds);
            }

            return;
        }

        var code = error is Win32Exception w ? w.NativeErrorCode : error.HResult & 0xFFFF;
        var ctx = root is null ? "" : DriveContext(root);
        if (root is not null && path.Length <= 3)
        {
            Log.Error(error, "드라이브 열기 실패 {Root} (Win32 {Code}, {Ms}ms). {Drive} {Env}", root, code, (int)elapsed.TotalMilliseconds, ctx, ProcessContext());
        }
        else
        {
            Log.Warning("폴더 열기 실패 {Path} (Win32 {Code}, {Ms}ms): {Message}. {Drive}", path, code, (int)elapsed.TotalMilliseconds, error.Message, ctx);
        }
    }

    public static string DriveContext(string root)
    {
        try
        {
            var type = Foldy.Platform.SystemInfo.Drives.TypeOf(root);
            var unc = type == Foldy.Platform.SystemInfo.NativeDriveType.Remote ? Foldy.Platform.SystemInfo.Drives.ToUnc(root) : "";
            var exists = Directory.Exists(root);
            // 서버·공유 이름은 회사 정보일 수 있어 남기지 않는다 (로컬 리다이렉터인지만)
            var uncKind = unc.Length == 0 ? "-" : unc.StartsWith(@"\localhost\", StringComparison.OrdinalIgnoreCase) || unc.StartsWith(@"\127.", StringComparison.Ordinal) ? "로컬 리다이렉터" : "네트워크 공유";
            return $"드라이브 종류={type}, UNC={uncKind}, 보임={exists}, 논리 드라이브={string.Join("", Environment.GetLogicalDrives().Select(d => d[0]))}";
        }
#pragma warning disable CA1031 // 진단 정보 수집이 실패해도 앱은 계속
        catch (Exception ex)
        {
            return "드라이브 정보 실패: " + ex.Message;
        }
#pragma warning restore CA1031
    }

    /// <summary>.NET 열거와 별개로 Win32 FindFirstFile로 직접 읽어 본 결과 (보안 프로그램 차단 여부 판단용).</summary>
    private static string RawEnumerate(string root)
    {
        try
        {
            var n = Directory.EnumerateFileSystemEntries(root).Take(5).Count();
            return $"직접 열거={n}개";
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            return $"직접 열거 실패 0x{ex.HResult:X8} {ex.GetType().Name}";
        }
#pragma warning restore CA1031
    }

    public static string ProcessContext()
    {
        using var id = WindowsIdentity.GetCurrent();
        var admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        var luid = GetLogonLuid();
        var lua = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", null);
        return $"관리자={admin}, UAC={(lua is int v ? (v != 0 ? "켜짐" : "꺼짐") : "?")}, 세션={GetSessionId()}, 로그온 LUID={luid}, 프로세스={Path.GetFileName(Environment.ProcessPath)}";
    }

    private static int GetSessionId()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.SessionId;
    }

    private static string GetLogonLuid()
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x0008, out var token))
            {
                return "?";
            }

            try
            {
                var buf = Marshal.AllocHGlobal(56);
                try
                {
                    // TokenStatistics(10): AuthenticationId는 TokenId(8바이트) 다음
                    if (!NativeMethods.GetTokenInformation(token, 10, buf, 56, out _))
                    {
                        return "?";
                    }

                    return Marshal.ReadInt64(buf, 8).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
#pragma warning disable CA1031
        catch
        {
            return "?";
        }
#pragma warning restore CA1031
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returned);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
