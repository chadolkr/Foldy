using System.Runtime.InteropServices;

namespace Foldy.Core.Search;

/// <summary>
/// Windows에서 폴더 하나의 목록을 한 번에 읽는다 (FindFirstFileEx, 큰 묶음으로 받기).
/// 목록 줄마다 재분석 지점의 종류(태그)가 함께 와서, 하위 폴더가 링크(정션·심볼릭 링크)인지 그 폴더를 따로 열지 않고 안다.
/// 2026-11-25 회사 클라우드 드라이브(K:): 폴더가 모두 재분석 지점이라, 예전에는 하위 폴더마다 한 번 더 열어 링크인지 물었다
/// (폴더 여는 데 0.1~1초인 드라이브에서 하위 폴더가 10개면 11번). Everything이 같은 폴더를 더 빨리 읽는 까닭으로 보았다.
/// </summary>
internal static class WinDirList
{
    private const uint ReparseTagMountPoint = 0xA0000003; // 정션
    private const uint ReparseTagSymlink = 0xA000000C;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;
    private static readonly nint Invalid = -1;

    /// <summary>
    /// 폴더 목록과 그 폴더 자신의 수정 시각("." 줄, 없으면 0). <paramref name="skipDir"/>은 이름으로 빼는 폴더(휴지통 등).
    /// 숨김+시스템 파일(desktop.ini·thumbs.db)과 링크 폴더는 뺀다. 클라우드 파일 폴더 같은 다른 재분석 지점은 진짜 폴더라 넣는다.
    /// 오류는 .NET 폴더 읽기와 같게: 없는 폴더는 DirectoryNotFoundException, 권한이 없으면 빈 목록, 그 밖은 IOException.
    /// </summary>
    public static List<CrawlEntry> List(string dir, Func<string, bool> skipDir, out long self)
    {
        self = 0;
        var list = new List<CrawlEntry>();
        var pattern = Path.Join(Extended(dir), "*");
        var h = FindFirstFileExW(pattern, FindExInfoBasic, out var d, FindExSearchNameMatch, 0, FindFirstExLargeFetch);
        if (h == Invalid)
        {
            var err = Marshal.GetLastPInvokeError();
            return err switch
            {
                ErrorAccessDenied => list,
                ErrorFileNotFound => list, // 빈 드라이브 맨 위 ("."도 없다)
                ErrorPathNotFound => throw new DirectoryNotFoundException(dir),
                _ => throw new IOException($"폴더 목록을 읽지 못함 ({err})", err),
            };
        }

        try
        {
            while (true)
            {
                var name = d.FileName;
                var attrs = (FileAttributes)d.Attributes;
                var isDir = (attrs & FileAttributes.Directory) != 0;
                var time = ((long)d.Written.High << 32) | d.Written.Low;
                if (name is "." or "..")
                {
                    self = name.Length == 1 ? DateTime.FromFileTimeUtc(time).Ticks : self;
                }
                else
                {
                    var skip = isDir
                        ? skipDir(name) || ((attrs & FileAttributes.ReparsePoint) != 0 && IsLinkTag(d.ReparseTag))
                        : (attrs & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);
                    if (!skip)
                    {
                        var size = isDir ? 0 : ((long)d.SizeHigh << 32) | d.SizeLow;
                        list.Add(new CrawlEntry(Path.Join(dir, name), isDir, size, DateTime.FromFileTimeUtc(time)));
                    }
                }

                if (!FindNextFileW(h, out d))
                {
                    var err = Marshal.GetLastPInvokeError();
                    if (err == ErrorNoMoreFiles)
                    {
                        break;
                    }

                    throw new IOException($"폴더 목록을 읽다 끊김 ({err})", err);
                }
            }
        }
        finally
        {
            FindClose(h);
        }

        return list;
    }

    /// <summary>260자보다 긴 위치도 읽게 \\?\ 를 붙인다 (.NET 폴더 읽기도 그렇게 한다).</summary>
    internal static string Extended(string dir)
    {
        if (dir.StartsWith(@"\\?\", StringComparison.Ordinal) || dir.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return dir;
        }

        return dir.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + dir[2..] : @"\\?\" + dir;
    }

    /// <summary>링크(정션·심볼릭 링크)로 보는 재분석 태그. 클라우드 파일·중복 제거 같은 다른 태그는 진짜 폴더다.</summary>
    internal static bool IsLinkTag(uint tag) => tag is ReparseTagMountPoint or ReparseTagSymlink;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public FileTime Created;
        public FileTime Accessed;
        public FileTime Written;
        public uint SizeHigh;
        public uint SizeLow;
        public uint ReparseTag; // 재분석 지점이면 그 태그
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint FindFirstFileExW(string fileName, int infoLevel, out FindData data, int searchOp, nint filter, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(nint handle, out FindData data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
}
