using System.IO.Compression;
using System.Security.Cryptography;

namespace Foldy.Core.Logs;

/// <summary>로그 파일 하나 (색인 또는 직접 훑기에서 얻은 정보).</summary>
public sealed record LogFile(string Path, long Size, DateTime ModifiedUtc);

/// <summary>같은 폴더·같은 달의 로그들. 같은 폴더의 logs-YYYY-MM.zip 하나로 묶는다.</summary>
public sealed record LogGroup(string Folder, string Month, IReadOnlyList<LogFile> Files)
{
    public long Size => Files.Sum(f => f.Size);

    public string ZipPath => System.IO.Path.Combine(Folder, $"logs-{Month}.zip");

    /// <summary>예상으로 줄어드는 크기. 텍스트 로그는 보통 85~95% 줄어서 보수적으로 85%로 잡는다.</summary>
    public long EstimatedSaving => (long)(Size * LogPlanner.SavingRatio);
}

/// <summary>로그 정리 대상 고르기: 7일 넘게 바뀌지 않은 *.log만, Windows 폴더와 시스템 폴더는 뺀다.</summary>
public static class LogPlanner
{
    public const int MinAgeDays = 7;
    public const double SavingRatio = 0.85;

    /// <summary>백그라운드 알림 기준: 줄일 수 있는 양이 1GB를 넘거나 그 드라이브 남은 공간의 5%를 넘을 때.</summary>
    public const long NoticeBytes = 1L << 30;
    public const double NoticeFreeRatio = 0.05;

    private static readonly string[] RootSkip = ["\\windows\\", "\\windows.old\\"];
    private static readonly string[] AnywhereSkip = ["\\$recycle.bin\\", "\\system volume information\\"];

    public static bool IsCandidate(LogFile f, DateTime nowUtc)
    {
        if (!f.Path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.Size <= 0)
        {
            return false;
        }

        if (nowUtc - f.ModifiedUtc < TimeSpan.FromDays(MinAgeDays))
        {
            return false;
        }

        // Windows 폴더는 볼륨 바로 아래일 때만 뺀다 (다른 곳의 "windows" 폴더는 사용자 폴더일 수 있다).
        var p = f.Path.Replace('/', '\\');
        var rel = p.Length > 2 && p[1] == ':' ? p[2..] : p;
        return !RootSkip.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase))
            && !AnywhereSkip.Any(s => rel.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>폴더별·달별로 묶는다. 큰 묶음이 먼저.</summary>
    public static IReadOnlyList<LogGroup> Plan(IEnumerable<LogFile> files, DateTime nowUtc) =>
        files.Where(f => IsCandidate(f, nowUtc))
            .GroupBy(f => (Folder: System.IO.Path.GetDirectoryName(f.Path) ?? "", Month: f.ModifiedUtc.ToLocalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)))
            .Where(g => g.Key.Folder.Length > 0)
            .Select(g => new LogGroup(g.Key.Folder, g.Key.Month, g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Size)
            .ToList();

    public static bool IsSerious(long saving, long freeBytes) =>
        saving >= NoticeBytes || (freeBytes > 0 && saving >= freeBytes * NoticeFreeRatio && saving >= 100L << 20);
}

/// <summary>압축 결과: zip에 들어가고 검증까지 끝나 지워도 되는 원본, 건너뛴 파일(사용 중·읽기 실패).</summary>
public sealed record LogArchiveResult(IReadOnlyList<string> Archived, IReadOnlyList<(string Path, string Reason)> Skipped, long ZipBytesAdded);

/// <summary>
/// 로그를 월별 zip에 넣는다. 다른 프로그램이 쓰고 있는 파일은 건너뛰고, zip을 다시 열어 내용이 원본과 같은지
/// (SHA-256) 확인한 파일만 Archived로 돌려준다. 원본은 여기서 지우지 않는다: 호출한 쪽이 휴지통으로 보낸다.
/// </summary>
public static class LogArchiver
{
    public static LogArchiveResult Archive(LogGroup group, CancellationToken ct)
    {
        var skipped = new List<(string, string)>();
        var added = new List<(string Path, string Entry, byte[] Hash)>();
        var before = File.Exists(group.ZipPath) ? new FileInfo(group.ZipPath).Length : 0;

        var existed = File.Exists(group.ZipPath);
        using (var zip = ZipFile.Open(group.ZipPath, existed ? ZipArchiveMode.Update : ZipArchiveMode.Create))
        {
            var names = new HashSet<string>(existed ? zip.Entries.Select(e => e.FullName) : [], StringComparer.OrdinalIgnoreCase);
            foreach (var f in group.Files)
            {
                ct.ThrowIfCancellationRequested();
                FileStream src;
                try
                {
                    // 다른 프로그램이 열어 두고 있으면(쓰는 중) 열리지 않는다 → 건너뜀
                    src = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.None);
                }
                catch (IOException)
                {
                    skipped.Add((f.Path, "다른 프로그램이 쓰는 중"));
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add((f.Path, "권한 없음"));
                    continue;
                }

                using (src)
                {
                    var entryName = UniqueName(names, System.IO.Path.GetFileName(f.Path));
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(f.Path);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var dst = entry.Open())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            sha.AppendData(buf, 0, n);
                            dst.Write(buf, 0, n);
                        }
                    }

                    names.Add(entryName);
                    added.Add((f.Path, entryName, sha.GetHashAndReset()));
                }
            }
        }

        // 다시 열어 확인: 같은 내용이어야만 원본을 지울 수 있다
        var ok = new List<string>();
        using (var zip = ZipFile.OpenRead(group.ZipPath))
        {
            foreach (var (path, entryName, hash) in added)
            {
                ct.ThrowIfCancellationRequested();
                var e = zip.GetEntry(entryName);
                if (e is not null && Hash(e).AsSpan().SequenceEqual(hash))
                {
                    ok.Add(path);
                }
                else
                {
                    skipped.Add((path, "압축 확인 실패 (원본은 그대로 둠)"));
                }
            }
        }

        if (added.Count == 0 && before == 0 && File.Exists(group.ZipPath))
        {
            File.Delete(group.ZipPath); // 아무것도 못 넣었으면 빈 zip을 남기지 않는다
        }

        var after = File.Exists(group.ZipPath) ? new FileInfo(group.ZipPath).Length : 0;
        return new LogArchiveResult(ok, skipped, after - before);
    }

    private static byte[] Hash(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return SHA256.HashData(s);
    }

    private static string UniqueName(HashSet<string> names, string name)
    {
        if (!names.Contains(name))
        {
            return name;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var n = $"{stem} ({i}){ext}";
            if (!names.Contains(n))
            {
                return n;
            }
        }
    }
}
