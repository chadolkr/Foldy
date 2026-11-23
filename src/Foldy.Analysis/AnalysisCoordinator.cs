namespace Foldy.Analysis;

/// <summary>최신 스냅샷 + 비교용 이전 스냅샷들 (최신순).</summary>
/// <param name="FromList">색인 없는 드라이브를 앱이 모아 둔 목록으로 만든 것 (드라이브를 다시 읽지 않음).</param>
public sealed record SnapshotSet(SnapshotRef Latest, IReadOnlyList<SnapshotRef> Previous, bool FromService, bool FromList = false);

/// <summary>
/// 색인 서비스(foldy-index)의 분석 DB. 구현은 Foldy.Indexer.Client의 IndexerAnalysisService (파이프 BuildAnalysis / AnalysisInfo).
/// 서비스가 그 볼륨을 다루지 않거나(비 NTFS, 서비스 없음) 색인이 준비되지 않았으면 null → 폴백 스캐너.
/// </summary>
public interface IAnalysisSnapshotService
{
    Task<SnapshotSet?> GetLatestAsync(string volume, CancellationToken ct);

    /// <summary>새 스냅샷을 만들고 끝날 때까지 기다린다. 서비스가 못 하면 null (실패하면 예외).</summary>
    Task<SnapshotSet?> BuildAsync(string volume, IProgress<AnalysisProgress>? progress, CancellationToken ct);
}

/// <summary>
/// 분석 탭이 쓰는 진입점: 서비스 DB가 있으면 그것을, 없으면 %LOCALAPPDATA% 폴백 DB를 찾고 만든다 (설계서 7.3).
/// </summary>
public sealed class AnalysisCoordinator(IAnalysisSnapshotService? service, CleanupRules? rules = null, string? localDirectory = null)
{
    public CleanupRules Rules { get; } = rules ?? CleanupRules.LoadUserOrDefault();

    public string LocalDirectory { get; } = localDirectory ?? AnalysisPaths.LocalDirectory;

    public IAnalysisSnapshotService? Service { get; } = service;

    /// <summary>
    /// 색인 서비스가 다루지 않는 드라이브(네트워크·보안 드라이브, FAT/exFAT, USB)에 대해 앱이 다 모아 둔 목록을 찾는다.
    /// 드라이브 뿌리("K:\")를 받아 목록을 돌려준다. 없으면(아직 다 모으지 못함·모으기 꺼짐) null → 폴더를 직접 읽는다.
    /// </summary>
    public Func<string, CrawledList?>? CrawledLists { get; init; }

    /// <summary>"C:\Users\me" → "C:" (서비스는 드라이브 단위로 색인). UNC 등은 null.</summary>
    public static string? DriveOf(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var r = root.Trim();
        return r.Length >= 2 && r[1] == ':' && char.IsAsciiLetter(r[0]) ? r[..2].ToUpperInvariant() : null;
    }

    public async Task<SnapshotSet?> FindLatestAsync(string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Service is not null && DriveOf(root) is { } drive)
        {
            try
            {
                if (await Service.GetLatestAsync(drive, ct).ConfigureAwait(false) is { } s)
                {
                    return s;
                }
            }
            catch (IOException)
            {
                // 서비스 연결 실패 → 로컬 폴백 DB를 본다
            }
        }

        var local = AnalysisPaths.ListSnapshots(LocalDirectory, AnalysisPaths.VolumeKey(root));
        return local.Count == 0 ? null : new SnapshotSet(local[0], [.. local.Skip(1)], false, AnalysisPaths.IsFromList(local[0].DbPath));
    }

    /// <summary>
    /// 분석을 새로 한다. 서비스가 그 드라이브를 다루면 서비스가(디스크를 다시 읽지 않음),
    /// 아니면 앱이 다 모아 둔 목록으로(디스크를 다시 읽지 않음), 그것도 없으면 폴백 스캐너가 폴더를 직접 읽는다.
    /// </summary>
    public async Task<SnapshotSet> AnalyzeAsync(string root, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Service is not null && DriveOf(root) is { } drive)
        {
            SnapshotSet? s = null;
            try
            {
                s = await Service.BuildAsync(drive, progress, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 연결이 끊겼으면 폴백
            }

            if (s is not null)
            {
                return s;
            }
        }

        // 색인 서비스가 없는 드라이브: 앱이 다 모아 둔 목록이 있으면 드라이브를 다시 읽지 않는다 (2026-11-23 K: 드라이브)
        if (FindCrawledList(root) is { } list)
        {
            var age = DateTime.UtcNow - list.CompletedUtc;
            progress?.Report(new AnalysisProgress(0.02, $"모아 둔 목록으로 분석하는 중 ({AnalysisText.Ago(age)} 목록, 드라이브를 다시 읽지 않습니다)…"));
            var (path, _) = await Task.Run(
                () =>
                {
                    var input = CrawledListSnapshot.Build(root, list.Read(ct), list.CompletedUtc, FallbackScanner.ClusterSizeOf(root), progress, ct);
                    return SnapshotBuilder.BuildIntoDirectory(input, LocalDirectory, AnalysisPaths.VolumeKey(root), Rules, progress, ct);
                },
                ct).ConfigureAwait(false);
            AnalysisPaths.MarkFromList(path);
            var made = AnalysisPaths.ListSnapshots(LocalDirectory, AnalysisPaths.VolumeKey(root));
            return new SnapshotSet(made[0], [.. made.Skip(1)], false, true);
        }
        else
        {
            progress?.Report(new AnalysisProgress(0, "폴더를 직접 읽는 중 (이 드라이브는 아직 다 모으지 못했습니다)…"));
            await FallbackScanner.ScanAsync(root, LocalDirectory, Rules, progress, ct, PreviousFileCount(root)).ConfigureAwait(false);
        }

        var local = AnalysisPaths.ListSnapshots(LocalDirectory, AnalysisPaths.VolumeKey(root));
        return new SnapshotSet(local[0], [.. local.Skip(1)], false);
    }

    /// <summary>지난번 이 폴더를 분석했을 때의 파일 수 (진행률 어림용). 없거나 못 읽으면 0.</summary>
    private long PreviousFileCount(string root)
    {
        try
        {
            var last = AnalysisPaths.ListSnapshots(LocalDirectory, AnalysisPaths.VolumeKey(root));
            if (last.Count == 0)
            {
                return 0;
            }

            using var db = AnalysisDb.Open(last[0].DbPath);
            return db.Scan().TotalFiles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
    }

    private CrawledList? FindCrawledList(string root)
    {
        if (CrawledLists is null || DriveOf(root) is not { } drive)
        {
            return null;
        }

        try
        {
            return CrawledLists(drive + "\\");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
