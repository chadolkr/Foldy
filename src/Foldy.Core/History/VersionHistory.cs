using System.Text.Json;

namespace Foldy.Core.History;

/// <summary>덮어쓰기 전에 보관해 둔 이전 버전 하나.</summary>
public sealed record SavedVersion(string Original, string Saved, DateTime SavedUtc, long Size);

/// <summary>
/// 덮어쓰기 복구: Foldy가 파일을 덮어쓰기 직전에 원래 파일을 같은 드라이브의 숨김 폴더
/// <c>X:\.foldy\이전 버전\연-월-일\</c>로 옮겨 둔다(같은 드라이브라 이름만 바뀌어 즉시 끝난다).
/// 7일이 지나거나 드라이브별 한도(2GB 또는 드라이브의 5% 중 작은 쪽)를 넘으면 오래된 것부터 지운다.
/// 한도보다 큰 파일은 보관하지 않는다.
/// </summary>
public static class VersionHistory
{
    public const string FolderName = ".foldy";
    public const string HistoryName = "이전 버전";
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    public const long MaxBytes = 2L << 30;

    private const string IndexName = "index.jsonl";
    private static readonly Lock Gate = new();

    /// <summary>보관 한도 (드라이브 크기를 알면 그 5%와 2GB 중 작은 쪽).</summary>
    public static long CapFor(string path)
    {
        try
        {
            var total = new DriveInfo(Path.GetPathRoot(path)!).TotalSize;
            return Math.Min(MaxBytes, total / 20);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return MaxBytes;
        }
    }

    /// <summary>테스트용: 보관 폴더 위치를 바꾼다.</summary>
    internal static Func<string, string>? RootOverride { get; set; }

    /// <summary>테스트용: 보관해도 되는 드라이브인지 판단을 바꾼다.</summary>
    internal static Func<string, bool>? LocalDriveOverride { get; set; }

    /// <summary>
    /// 보관해도 되는 드라이브인가: 이 PC에 붙은 고정 디스크(DriveType.Fixed)만.
    /// 네트워크 드라이브·USB에 두면 (1) 덮어쓴 파일의 예전 내용이 공유 폴더 맨 위나 남에게 건넨 USB에 7일 동안 남고,
    /// (2) 같은 공유를 쓰는 다른 사람이 index.jsonl을 바꿔 이 PC의 다른 파일을 지우게 만들 수 있다 (보안 리뷰 2026-11-22 V1).
    /// </summary>
    public static bool IsLocalDrive(string path)
    {
        if (LocalDriveOverride is { } o)
        {
            return o(path);
        }

        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 기록 한 줄을 믿어도 되나: 보관본은 이 드라이브의 보관 폴더 안, 원래 파일은 같은 드라이브여야 한다.
    /// index.jsonl은 사용자가 아닌 누군가가 고칠 수 있는 파일일 수 있으므로, 지우기·옮기기 전에 꼭 확인한다.
    /// </summary>
    internal static bool IsTrusted(SavedVersion v, string historyRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(v.Saved) || string.IsNullOrEmpty(v.Original) || !Path.IsPathFullyQualified(v.Saved) || !Path.IsPathFullyQualified(v.Original))
            {
                return false;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(historyRoot)) + Path.DirectorySeparatorChar;
            var saved = Path.GetFullPath(v.Saved);
            if (!saved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || saved.Length <= root.Length)
            {
                return false;
            }

            // 원래 파일의 보관 폴더가 바로 이 폴더여야 한다 (다른 드라이브의 파일을 가리키는 기록은 받지 않는다)
            var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(HistoryRoot(v.Original))) + Path.DirectorySeparatorChar;
            return string.Equals(own, root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// 보관 폴더. 같은 드라이브여야 옮기기가 즉시 끝난다. 시스템 드라이브(C:)는 루트에 쓰지 않고
    /// 사용자 폴더(%LOCALAPPDATA%\Foldy\이전 버전) 안에 둔다.
    /// </summary>
    public static string HistoryRoot(string path)
    {
        if (RootOverride is { } o)
        {
            return o(path);
        }

        var root = Path.GetPathRoot(path)!;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.Equals(Path.GetPathRoot(local), root, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(local, "Foldy", HistoryName)
            : Path.Combine(root, FolderName, HistoryName);
    }

    /// <summary>
    /// 덮어쓸 파일을 보관한다. 보관했으면 그 기록, 못 했으면(파일 없음, 너무 큼, 권한 없음, 로컬 드라이브 아님) null.
    /// </summary>
    public static SavedVersion? Preserve(string file, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || file.Length < 3 || file[1] != ':' || !IsLocalDrive(file) || fi.Length > CapFor(file))
            {
                return null;
            }

            var root = HistoryRoot(file);
            var day = Path.Combine(root, now.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(day);
            HideFoldyFolder(file);
            var stamp = now.ToLocalTime().ToString("HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var saved = Path.Combine(day, $"{Path.GetFileNameWithoutExtension(file)} ({stamp}){Path.GetExtension(file)}");
            for (var i = 2; File.Exists(saved); i++)
            {
                saved = Path.Combine(day, $"{Path.GetFileNameWithoutExtension(file)} ({stamp}-{i}){Path.GetExtension(file)}");
            }

            File.Move(file, saved);
            var v = new SavedVersion(Path.GetFullPath(file), saved, now, fi.Length);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(root, IndexName), JsonSerializer.Serialize(v) + "\n");
            }

            Prune(file, now);
            return v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>이 파일의 이전 버전들 (최근 것 먼저, 보관 파일이 남아 있는 것만).</summary>
    public static IReadOnlyList<SavedVersion> VersionsOf(string file) =>
        All(file).Where(v => string.Equals(v.Original, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) && File.Exists(v.Saved))
            .OrderByDescending(v => v.SavedUtc).ToList();

    /// <summary>
    /// 되돌린다: 지금 파일도 먼저 보관한 뒤 이전 버전을 제자리로 옮긴다 (되돌리기도 되돌릴 수 있다).
    /// </summary>
    public static bool Restore(SavedVersion v)
    {
        try
        {
            if (!IsTrusted(v, HistoryRoot(v.Original)) || !File.Exists(v.Saved))
            {
                return false;
            }

            if (File.Exists(v.Original))
            {
                Preserve(v.Original);
            }

            if (File.Exists(v.Original))
            {
                return false; // 보관하지 못했으면 지금 파일을 지키려고 멈춘다
            }

            Directory.CreateDirectory(Path.GetDirectoryName(v.Original)!);
            File.Move(v.Saved, v.Original);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>옆에 꺼낸다: 원래 파일 옆에 "이름 (이전 버전 연-월-일 시분).확장자"로 복사. 만든 경로.</summary>
    public static string? ExtractBeside(SavedVersion v)
    {
        try
        {
            if (!IsTrusted(v, HistoryRoot(v.Original)))
            {
                return null;
            }

            var dir = Path.GetDirectoryName(v.Original)!;
            var when = v.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
            var dst = Path.Combine(dir, Foldy.Core.Localization.Loc.F("vh.name", Path.GetFileNameWithoutExtension(v.Original), when, Path.GetExtension(v.Original)));
            for (var i = 2; File.Exists(dst); i++)
            {
                dst = Path.Combine(dir, Foldy.Core.Localization.Loc.F("vh.nameN", Path.GetFileNameWithoutExtension(v.Original), when, i, Path.GetExtension(v.Original)));
            }

            File.Copy(v.Saved, dst);
            return dst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>7일이 지났거나 한도를 넘은 오래된 보관본을 지운다.</summary>
    public static void Prune(string anyPathOnDrive, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var root = HistoryRoot(anyPathOnDrive);
        lock (Gate)
        {
            var all = All(anyPathOnDrive).Where(v => File.Exists(v.Saved)).OrderBy(v => v.SavedUtc).ToList();
            var cap = CapFor(anyPathOnDrive);
            var total = all.Sum(v => v.Size);
            var keep = new List<SavedVersion>();
            foreach (var v in all)
            {
                if (now - v.SavedUtc > Keep || total > cap)
                {
                    try
                    {
                        File.Delete(v.Saved);
                        total -= v.Size;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        keep.Add(v);
                    }
                }
                else
                {
                    keep.Add(v);
                }
            }

            try
            {
                if (Directory.Exists(root))
                {
                    File.WriteAllLines(Path.Combine(root, IndexName), keep.Select(v => JsonSerializer.Serialize(v)));
                    foreach (var d in Directory.EnumerateDirectories(root).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
                    {
                        Directory.Delete(d);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static List<SavedVersion> All(string anyPathOnDrive)
    {
        var index = Path.Combine(HistoryRoot(anyPathOnDrive), IndexName);
        var list = new List<SavedVersion>();
        try
        {
            if (!File.Exists(index))
            {
                return list;
            }

            foreach (var line in File.ReadLines(index))
            {
                if (line.Length > 0)
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<SavedVersion>(line) is { } v && IsTrusted(v, HistoryRoot(anyPathOnDrive)))
                        {
                            list.Add(v);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return list;
    }

    private static void HideFoldyFolder(string anyPathOnDrive)
    {
        var d = new DirectoryInfo(Path.Combine(Path.GetPathRoot(anyPathOnDrive)!, FolderName));
        if (!HistoryRoot(anyPathOnDrive).StartsWith(d.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (d.Exists && (d.Attributes & FileAttributes.Hidden) == 0)
        {
            d.Attributes |= FileAttributes.Hidden;
        }
    }
}
