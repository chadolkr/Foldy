using System.Collections.Concurrent;
using Foldy.Platform.Interop;

namespace Foldy.Platform.Shell;

/// <summary>파일 연결 조회 (설계서 5.9: AssocQueryStringW, 확장자별 캐시).</summary>
public static class Associations
{
    private const uint ASSOCF_INIT_IGNOREUNKNOWN = 0x00000400;
    private const uint ASSOCSTR_EXECUTABLE = 2;
    private static readonly ConcurrentDictionary<string, (string? Exe, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>확장자에 연결된 실행 파일. 없으면 null ("연결 프로그램 선택" 창만 뜨는 경우 포함).</summary>
    public static string? Executable(string extension)
    {
        if (Cache.TryGetValue(extension, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1))
        {
            return c.Exe;
        }

        string? exe = null;
        uint len = 0;
        if (Native.AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, null, null, ref len) is 1 or 0 && len > 0)
        {
            var buf = new char[len];
            if (Native.AssocQueryStringW(ASSOCF_INIT_IGNOREUNKNOWN, ASSOCSTR_EXECUTABLE, extension, null, buf, ref len) == 0)
            {
                exe = new string(buf).TrimEnd('\0');
                if (exe.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exe = null;
                }
            }
        }

        Cache[extension] = (exe, DateTime.UtcNow);
        return exe;
    }

    /// <summary>파일 연결이 바뀌었을 때 (SHCNE_ASSOCCHANGED) 캐시를 비운다.</summary>
    public static void Invalidate() => Cache.Clear();
}
