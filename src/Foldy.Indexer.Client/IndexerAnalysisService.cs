using Foldy.Analysis;

namespace Foldy.Indexer.Client;

/// <summary>
/// 색인 서비스의 분석 DB를 <see cref="IAnalysisSnapshotService"/>로 감싼다 (BuildAnalysis / AnalysisInfo 파이프 메시지).
/// 호출마다 연결 함수로 클라이언트를 얻는다 — 호스트가 이미 가진 연결을 넘기거나, 기본값으로 매번 새로 연결한다.
/// 서비스가 없거나 그 볼륨을 다루지 않거나 색인이 준비되지 않았으면 null을 돌려 폴백 스캐너로 넘어가게 한다.
/// </summary>
public sealed class IndexerAnalysisService(Func<CancellationToken, Task<IndexerClient?>>? connect = null, bool disposeClient = true) : IAnalysisSnapshotService
{
    private readonly Func<CancellationToken, Task<IndexerClient?>> _connect =
        connect ?? (ct => IndexerClient.TryConnectAsync(Protocol.DefaultPipeName, TimeSpan.FromMilliseconds(500), ct));

    /// <summary>진행률을 물어보는 간격.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(300);

    public async Task<SnapshotSet?> GetLatestAsync(string volume, CancellationToken ct)
    {
        var client = await _connect(ct).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        try
        {
            return ToSet(await client.AnalysisInfoAsync(volume, ct).ConfigureAwait(false));
        }
        catch (ProtocolException)
        {
            return null; // 분석 메시지를 모르는 옛 서비스
        }
        finally
        {
            if (disposeClient)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<SnapshotSet?> BuildAsync(string volume, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var client = await _connect(ct).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        try
        {
            BuildAnalysisReply start;
            try
            {
                start = await client.BuildAnalysisAsync(volume, ct).ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
                return null;
            }

            if (start.Status is not (AnalysisStatus.Ok or AnalysisStatus.AlreadyRunning))
            {
                return null;
            }

            progress?.Report(new AnalysisProgress(0, "색인 서비스가 분석하는 중…"));
            while (true)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                var st = await client.StatusAsync(ct).ConfigureAwait(false);
                var a = st.Analysis.FirstOrDefault(x => string.Equals(x.Volume.TrimEnd('\\'), volume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (a is null)
                {
                    continue;
                }

                if (a.State == AnalysisJobState.Building)
                {
                    progress?.Report(new AnalysisProgress(a.ProgressPermille / 1000.0, a.Message));
                    continue;
                }

                if (a.State == AnalysisJobState.Failed)
                {
                    throw new InvalidOperationException("색인 서비스 분석 실패: " + a.Message);
                }

                if (a.State == AnalysisJobState.Ready)
                {
                    progress?.Report(new AnalysisProgress(1, a.Message));
                    return ToSet(await client.AnalysisInfoAsync(volume, ct).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            if (disposeClient)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static SnapshotSet? ToSet(AnalysisInfoReply r) =>
        r.Status != AnalysisStatus.Ok || r.DbPath.Length == 0
            ? null
            : new SnapshotSet(new SnapshotRef(r.DbPath, r.ScanTimeUtc), [.. r.Previous.Select(p => new SnapshotRef(p.DbPath, p.ScanTimeUtc))], true);
}
