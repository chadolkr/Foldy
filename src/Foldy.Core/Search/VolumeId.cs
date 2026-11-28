using System.Runtime.InteropServices;

namespace Foldy.Core.Search;

/// <summary>
/// 드라이브(볼륨)의 일련번호 (1.18.1, 2026-11-28). 외장하드를 빼고 다른 외장하드를 꽂으면 Windows가 같은 글자(E:)를 다시 준다
/// (사용자 제보). 글자만으로 목록을 저장하면 앞 디스크의 목록이 새 디스크에 섞여 보인다. 그래서 목록은 글자 + 일련번호로 구분한다.
/// 일련번호는 포맷할 때 정해지고, 같은 디스크면 어느 글자로 꽂혀도 같다.
/// </summary>
internal static class VolumeId
{
    /// <summary>그 위치가 들어 있는 볼륨의 일련번호. 알 수 없으면(빠진 드라이브, 오류) null.</summary>
    public static uint? Serial(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        if (!root.EndsWith('\\'))
        {
            root += "\\";
        }

        return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0) ? serial : null;
    }

    /// <summary>네트워크 드라이브인가 (드라이브에 묻지 않는다: 끊긴 서버에서도 바로 답한다).</summary>
    public static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        return OperatingSystem.IsWindows() && !string.IsNullOrEmpty(root) && GetDriveTypeW(root.EndsWith('\\') ? root : root + "\\") == DriveRemote;
    }

    private const uint DriveRemote = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, char[]? name, int nameSize, out uint serial, out uint maxComponent, out uint flags, char[]? fileSystem, int fileSystemSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDriveTypeW(string root);
}
