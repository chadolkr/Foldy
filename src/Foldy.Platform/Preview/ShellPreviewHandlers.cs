using System.Collections.Concurrent;
using Foldy.Platform.Interop;
using Microsoft.Win32;

namespace Foldy.Platform.Preview;

/// <summary>
/// 처리기 CLSID별 연속 실패 기록 (설계서 8.3: 셸 확장은 크래시 원인 1순위 → 연속 실패하면 쓰지 않는다).
/// 성공하면 횟수를 지운다. 스레드 안전. 앱 실행 동안만 기억한다.
/// </summary>
public sealed class PreviewHandlerBlacklist(int threshold = 3)
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    public int Threshold { get; } = threshold;

    public bool IsBlocked(Guid clsid) => _failures.TryGetValue(clsid, out var n) && n >= Threshold;

    public int FailureCount(Guid clsid) => _failures.TryGetValue(clsid, out var n) ? n : 0;

    /// <summary>실패를 기록하고, 이번 실패로 차단됐으면 true.</summary>
    public bool RecordFailure(Guid clsid) => _failures.AddOrUpdate(clsid, 1, (_, n) => n + 1) >= Threshold;

    public void RecordSuccess(Guid clsid) => _failures.TryRemove(clsid, out _);

    public void Clear() => _failures.Clear();
}

/// <summary>
/// 확장자 → Windows 미리보기 처리기 조회. 탐색기와 같은 기준: HKCR\.ext\ShellEx\{8895b1c6-…}(AssocQueryString)로
/// CLSID를 찾고, 승인 목록(PreviewHandlers 키)에 있는 것만 쓴다.
/// </summary>
public static class ShellPreviewHandlers
{
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers";

    private static readonly ConcurrentDictionary<string, Guid?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>앱 전체가 공유하는 블랙리스트.</summary>
    public static PreviewHandlerBlacklist Blacklist { get; } = new();

    /// <summary>이 확장자에 쓸 수 있는 처리기가 있고 차단되지 않았는지. 레지스트리만 읽는다 (파일을 열지 않음).</summary>
    public static bool IsAvailable(string extension) =>
        FindHandler(extension) is { } clsid && !Blacklist.IsBlocked(clsid);

    /// <summary>승인된 처리기 CLSID. 없으면 null. 확장자별로 캐시한다.</summary>
    public static Guid? FindHandler(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.')
        {
            return null;
        }

        return Cache.GetOrAdd(extension, static ext => Lookup(ext));
    }

    /// <summary>파일 연결이 바뀌었을 때 캐시를 비운다.</summary>
    public static void Invalidate() => Cache.Clear();

    private static Guid? Lookup(string ext)
    {
        try
        {
            uint len = 0;
            var rc = Native.AssocQueryStringW(0, PreviewNative.ASSOCSTR_SHELLEXTENSION, ext, PreviewNative.PreviewHandlerShellExKey, null, ref len);
            if (rc is not (0 or 1) || len == 0)
            {
                return null;
            }

            var buf = new char[len];
            if (Native.AssocQueryStringW(0, PreviewNative.ASSOCSTR_SHELLEXTENSION, ext, PreviewNative.PreviewHandlerShellExKey, buf, ref len) != 0)
            {
                return null;
            }

            return Guid.TryParse(new string(buf).TrimEnd('\0'), out var clsid) && IsApproved(clsid) ? clsid : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool IsApproved(Guid clsid)
    {
        var name = clsid.ToString("B");
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(ApprovedKey);
            if (key?.GetValue(name) is not null)
            {
                return true;
            }
        }

        return false;
    }
}
