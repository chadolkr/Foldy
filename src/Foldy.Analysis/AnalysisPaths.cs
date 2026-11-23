using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Foldy.Analysis;

/// <summary>스냅샷 파일 하나 (최신순 목록의 항목).</summary>
public sealed record SnapshotRef(string DbPath, DateTime ScanTimeUtc);

/// <summary>
/// 스냅샷 파일 위치·이름·보관 정책 (설계서 7.3). Rust foldy-index analysis 모듈과 같은 규칙:
/// <c>&lt;볼륨 키&gt;.yyyyMMddTHHmmssZ.db</c>, 최신 1개 + 이전 최대 4개(바로 이전 1개 + 주 1회 간격).
/// </summary>
public static partial class AnalysisPaths
{
    public const int MaxHistory = 4;

    /// <summary>서비스가 만드는 DB 폴더 %ProgramData%\Foldy\Analysis (앱은 읽기 전용).</summary>
    public static string ServiceDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Foldy", "Analysis");

    /// <summary>폴백 스캐너가 만드는 DB 폴더 %LOCALAPPDATA%\Foldy\Analysis.</summary>
    public static string LocalDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "Analysis");

    /// <summary>
    /// 볼륨 키: "C:"·"C:\"는 볼륨 GUID("{…}", 소문자), 못 얻으면 드라이브 문자("C"),
    /// 그 밖의 경로는 "dir-" + 소문자 경로 UTF-8의 FNV-1a 64 (16진수 16자리).
    /// </summary>
    public static string VolumeKey(string volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var v = volume.Trim().Replace('/', '\\').TrimEnd('\\');
        if (v.Length == 2 && v[1] == ':' && char.IsAsciiLetter(v[0]))
        {
            return VolumeGuid(v + "\\") ?? v[..1].ToUpperInvariant();
        }

        var h = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(v.ToLowerInvariant()))
        {
            h ^= b;
            h *= 0x100000001b3UL;
        }

        return "dir-" + h.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>분석할 루트에 맞는 볼륨 이름: "C:\Users" → "C:", UNC·기타 → 그대로.</summary>
    public static string VolumeOf(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var r = root.Trim().Replace('/', '\\');
        return r.Length >= 2 && r[1] == ':' && char.IsAsciiLetter(r[0]) && r.TrimEnd('\\').Length == 2
            ? r[..2].ToUpperInvariant()
            : r.TrimEnd('\\');
    }

    public static string SnapshotFileName(string key, DateTime utc)
    {
        var t = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return key + "." + t.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".db";
    }

    public static DateTime? ParseStamp(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;

    /// <summary>폴더의 이 볼륨 키 스냅샷들, 최신순.</summary>
    public static IReadOnlyList<SnapshotRef> ListSnapshots(string directory, string key)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var prefix = key + ".";
        var list = new List<SnapshotRef>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory, prefix + "*.db"))
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".db", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ParseStamp(name[prefix.Length..^3]) is { } t)
                {
                    list.Add(new SnapshotRef(f, t));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        list.Sort((a, b) => b.ScanTimeUtc.CompareTo(a.ScanTimeUtc));
        return list;
    }

    /// <summary>지울 스냅샷 (최신순 입력). Rust retention_plan과 같다.</summary>
    public static IReadOnlyList<SnapshotRef> RetentionPlan(IReadOnlyList<SnapshotRef> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var week = TimeSpan.FromDays(6.5);
        var del = new List<SnapshotRef>();
        var kept = 0;
        DateTime? last = null;
        for (var i = 1; i < newestFirst.Count; i++)
        {
            var s = newestFirst[i];
            var keep = last is null || (kept < MaxHistory && last.Value - s.ScanTimeUtc >= week);
            if (keep && kept < MaxHistory)
            {
                kept++;
                last = s.ScanTimeUtc;
            }
            else
            {
                del.Add(s);
            }
        }

        return del;
    }

    /// <summary>모아 둔 목록으로 만든 스냅샷 옆에 두는 표시 파일 (스냅샷 DB 형식은 그대로 둔다).</summary>
    public static string ListMarkerOf(string dbPath) => dbPath + ".list";

    public static bool IsFromList(string dbPath) => File.Exists(ListMarkerOf(dbPath));

    internal static void MarkFromList(string dbPath)
    {
        try
        {
            File.WriteAllText(ListMarkerOf(dbPath), "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 표시만 못 할 뿐 분석 결과는 그대로 쓴다 ("직접 열거"로 보인다)
        }
    }

    private static string? VolumeGuid(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buf = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(root, buf, buf.Length))
        {
            return null;
        }

        var s = new string(buf, 0, Array.IndexOf(buf, '\0') is var n and >= 0 ? n : buf.Length);
        var a = s.IndexOf('{', StringComparison.Ordinal);
        var z = a < 0 ? -1 : s.IndexOf('}', a);
        return z > a ? s[a..(z + 1)].ToLowerInvariant() : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, int length);
}
