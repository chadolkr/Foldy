using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Foldy.Platform.Interop;

namespace Foldy.Platform.Shell;

/// <summary>
/// 셸 아이콘·종류 이름 (SHGetFileInfo). 확장자별 결과는 캐시하고, SHGFI_USEFILEATTRIBUTES로
/// 디스크에 접근하지 않는다(느린 드라이브에서도 안전). exe·lnk·ico처럼 파일마다 다른 아이콘은 PerFile로 따로 요청.
/// 반환된 HICON의 수명은 호출자가 DestroyIcon으로 끝낸다 (UI 쪽에서 BitmapSource로 바꾼 직후).
/// </summary>
public static class ShellInfo
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>파일마다 아이콘이 다른 확장자 (내용을 읽어야 함 → 보이는 행만, 느린 드라이브는 제외).</summary>
    public static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".msc", ".scr" };

    public static string TypeName(string name, bool isFolder)
    {
        if (isFolder)
        {
            return "파일 폴더";
        }

        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext))
        {
            return "파일";
        }

        return TypeNames.GetOrAdd(ext, e =>
        {
            var info = default(SHFILEINFOW);
            var r = Native.SHGetFileInfoW("x" + e, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            return r != IntPtr.Zero && !string.IsNullOrEmpty(info.szTypeName) ? info.szTypeName : e.TrimStart('.').ToUpperInvariant() + " 파일";
        });
    }

    /// <summary>확장자(또는 폴더) 기준 아이콘. 디스크 접근 없음.</summary>
    public static IntPtr GenericIcon(string extensionOrEmpty, bool isFolder, bool small = true)
    {
        var info = default(SHFILEINFOW);
        var name = isFolder ? "folder" : "x" + extensionOrEmpty;
        var attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        Native.SHGetFileInfoW(name, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON));
        return info.hIcon;
    }

    /// <summary>파일 자체의 아이콘 (exe 등). 디스크를 읽으므로 드라이브 큐/백그라운드 STA에서만 호출.</summary>
    public static IntPtr FileIcon(string path, bool small = true)
    {
        var info = default(SHFILEINFOW);
        Native.SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON));
        return info.hIcon;
    }

    public static void DestroyIcon(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(hIcon);
        }
    }
}
