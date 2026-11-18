using System.Text.Json;

namespace Foldy.Reporting;

/// <summary>
/// 크래시를 디스크에 먼저 남기고, 다음 실행 때 사용자 동의를 받아 보낸다.
/// 크래시 순간에는 UI도 네트워크도 믿을 수 없으므로 여기서는 파일 쓰기만 한다.
/// </summary>
public sealed class CrashStore(string directory)
{
    /// <summary>쌓아 둘 최대 건수. 같은 예외가 루프에서 터져도 디스크를 채우지 않게.</summary>
    public const int MaxPending = 20;

    public string Directory { get; } = directory;

    /// <summary>예외 처리기 안에서 부른다. 절대 던지지 않는다.</summary>
    public bool TrySave(CrashReport report, bool fatal, DateTimeOffset? now = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var existing = Pending();
            // 같은 예외(타입·상위 프레임)가 이미 대기 중이면 하나만 둔다
            if (existing.Any(p => SameCrash(p.Report, report))) return false;
            if (existing.Count >= MaxPending) return false;

            var at = now ?? DateTimeOffset.UtcNow;
            var path = Path.Combine(Directory, $"crash-{at:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new StoredCrash(report, fatal, at), ReportJson.Default.StoredCrash));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
#pragma warning disable CA1031 // 크래시 경로: 무엇이 실패해도 원래 예외 처리를 막지 않는다
        catch
        {
            return false;
        }
#pragma warning restore CA1031
    }

    public IReadOnlyList<StoredCrash> Pending()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<StoredCrash>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "crash-*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var item = JsonSerializer.Deserialize(File.ReadAllText(file), ReportJson.Default.StoredCrash);
                if (item?.Report is not null) list.Add(item with { FilePath = file });
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                TryDelete(file); // 깨진 파일은 버린다
            }
        }
        return list;
    }

    public void Remove(StoredCrash crash)
    {
        ArgumentNullException.ThrowIfNull(crash);
        // 이 저장소가 읽은 파일만 지운다
        if (crash.FilePath.Length > 0 &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(crash.FilePath)), Path.GetFullPath(Directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            TryDelete(crash.FilePath);
    }

    public void Clear()
    {
        foreach (var c in Pending()) Remove(c);
    }

    private static bool SameCrash(CrashReport a, CrashReport b) =>
        a.ExceptionType == b.ExceptionType && a.Frames.Take(5).SequenceEqual(b.Frames.Take(5));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
