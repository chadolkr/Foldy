using System.Runtime.InteropServices;

namespace Foldy.Platform.Shell;

/// <summary>Windows 휴지통: 모든 드라이브의 크기·개수 조회와 비우기.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential)] // x64: cbSize 뒤 8바이트 정렬 (크기 24)
    private struct ShQueryRbInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    /// <summary>모든 드라이브 휴지통의 합계. 조회 실패면 null.</summary>
    public static (long Bytes, long Items)? Query()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.Bytes, info.Items) : null;
    }

    /// <summary>모든 드라이브의 휴지통을 비운다 (확인은 호출자가 먼저 받는다). 성공하면 true.</summary>
    public static bool Empty(IntPtr owner)
    {
        var hr = SHEmptyRecycleBin(owner, null, NoConfirmation | NoSound);
        return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED = 이미 비어 있음
    }
}
