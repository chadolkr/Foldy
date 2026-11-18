using Foldy.Platform.Interop;

namespace Foldy.Platform.SystemInfo;

public enum NativeDriveType : uint { Unknown = 0, NoRootDir = 1, Removable = 2, Fixed = 3, Remote = 4, CdRom = 5, RamDisk = 6 }

public sealed record VolumeDetails(string? Label, string? FileSystem, ulong? Free, ulong? Total);

/// <summary>
/// 드라이브 정보. <see cref="List"/>는 GetLogicalDrives + GetDriveType만 써서 디스크·네트워크에 접근하지 않으므로
/// 끊긴 SMB 드라이브가 있어도 즉시 끝난다 (DESIGN.md 드라이브 바). <see cref="Details"/>는 블로킹이므로
/// 반드시 해당 드라이브의 작업 큐에서 호출한다.
/// </summary>
public static class Drives
{
    public static IReadOnlyList<(string Root, NativeDriveType Type)> List()
    {
        var mask = Native.GetLogicalDrives();
        var list = new List<(string, NativeDriveType)>();
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                var root = $"{(char)('A' + i)}:\\";
                list.Add((root, (NativeDriveType)Native.GetDriveTypeW(root)));
            }
        }

        return list;
    }

    public static NativeDriveType TypeOf(string root) => (NativeDriveType)Native.GetDriveTypeW(root);

    public static VolumeDetails Details(string root)
    {
        var name = new char[261];
        var fs = new char[261];
        string? label = null, fsName = null;
        if (Native.GetVolumeInformationW(root, name, name.Length, out _, out _, out _, fs, fs.Length))
        {
            label = new string(name).TrimEnd('\0');
            fsName = new string(fs).TrimEnd('\0');
        }

        ulong? free = null, total = null;
        if (Native.GetDiskFreeSpaceExW(root, out var f, out var t, out _))
        {
            free = f;
            total = t;
        }

        return new VolumeDetails(string.IsNullOrEmpty(label) ? null : label, fsName, free, total);
    }

    /// <summary>매핑된 네트워크 드라이브(Z:\...)를 UNC(\\server\share\...)로 바꾼다. 관리자 셸은 매핑을 못 보기 때문.</summary>
    public static string ToUnc(string path)
    {
        if (path.Length < 2 || path[1] != ':' || TypeOf(path[..2] + "\\") != NativeDriveType.Remote)
        {
            return path;
        }

        var buf = new char[1024];
        var len = buf.Length;
        if (Native.WNetGetConnectionW(path[..2], buf, ref len) != 0)
        {
            return path;
        }

        var unc = new string(buf).TrimEnd('\0');
        return unc + path[2..];
    }

    /// <summary>워치독용: 다른 스레드의 동기 I/O를 끊는다 (설계서 5.2, CancelSynchronousIo).</summary>
    public static bool CancelSynchronousIo(uint nativeThreadId)
    {
        const uint THREAD_TERMINATE = 0x0001;
        var h = Native.OpenThread(THREAD_TERMINATE, false, nativeThreadId);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Native.CancelSynchronousIo(h);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static uint CurrentNativeThreadId() => Native.GetCurrentThreadId();
}
