using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Foldy.Indexer.Client;

/// <summary>
/// 색인 서비스 클라이언트. 연결·버전 확인에 실패하면 <see cref="TryConnectAsync"/>가 null을 반환하고,
/// 호출자는 LocalProvider로 폴백한다 (설계서 5.6, 5.8).
/// </summary>
/// <remarks>
/// 읽기 루프 하나가 모든 응답을 받는다. Search 이외 요청의 응답은 보낸 순서대로 오므로 대기열로 짝을 맞추고,
/// SearchBatch는 search_id로 진행 중인 검색에 넘긴다. 새 검색을 시작하면 이전 검색은 서비스에서 자동 취소되고
/// 클라이언트 쪽 스트림은 <see cref="SearchStatus.Cancelled"/> 배치로 끝난다.
/// </remarks>
public sealed class IndexerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource<(MsgType Type, byte[] Body)>> _pending = new();
    private Task _reader = Task.CompletedTask;
    private ActiveSearch? _search;
    private uint _lastSearchId;
    private Exception? _fault;

    private IndexerClient(NamedPipeClientStream pipe, HelloReply hello)
    {
        _pipe = pipe;
        Hello = hello;
    }

    public HelloReply Hello { get; }

    /// <summary>연결이 끊겼거나 프로토콜 오류로 더 쓸 수 없으면 true.</summary>
    public bool IsFaulted
    {
        get
        {
            lock (_gate)
            {
                return _fault is not null;
            }
        }
    }

    /// <summary>연결하고 Hello로 버전을 맞춘다. 서비스가 없거나 버전이 다르면 null.</summary>
    public static async Task<IndexerClient?> TryConnectAsync(
        string pipeName = Protocol.DefaultPipeName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(500));
            await pipe.ConnectAsync(cts.Token);

            var (type, body) = await HandshakeAsync(
                pipe, Protocol.Encode(new Hello(Protocol.Version, $"Foldy {typeof(IndexerClient).Assembly.GetName().Version}")), cts.Token);
            if (type != MsgType.HelloReply)
            {
                await pipe.DisposeAsync();
                return null;
            }

            var hello = Protocol.DecodeHelloReply(body);
            if (hello.Protocol != Protocol.Version)
            {
                await pipe.DisposeAsync();
                return null;
            }

            var client = new IndexerClient(pipe, hello);
            client._reader = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            return client;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct = default) =>
        Protocol.DecodeStatusReply(await RequestAsync(Protocol.EncodeStatus(), MsgType.StatusReply, ct));

    /// <summary>폴더 바로 아래 항목의 크기 (폴더는 하위 전체). 색인 밖 경로면 Status가 NotIndexed.</summary>
    public async Task<FolderSizesReply> FolderSizesAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Protocol.DecodeFolderSizesReply(await RequestAsync(Protocol.EncodeFolderSizes(path), MsgType.FolderSizesReply, ct));
    }

    /// <summary>분석 DB 만들기를 시작한다 (설계서 7.3). 바로 돌아오고, 진행률은 <see cref="StatusAsync"/>의 Analysis로 본다.</summary>
    public async Task<BuildAnalysisReply> BuildAnalysisAsync(string volume, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return Protocol.DecodeBuildAnalysisReply(await RequestAsync(Protocol.EncodeBuildAnalysis(volume), MsgType.BuildAnalysisReply, ct));
    }

    /// <summary>볼륨의 최신 분석 DB 경로·분석 시각과 비교용 이전 스냅샷.</summary>
    public async Task<AnalysisInfoReply> AnalysisInfoAsync(string volume, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return Protocol.DecodeAnalysisInfoReply(await RequestAsync(Protocol.EncodeAnalysisInfo(volume), MsgType.AnalysisInfoReply, ct));
    }

    /// <summary>
    /// 이름 부분 문자열 검색(대소문자 무시) 결과를 배치 단위로 받는다. 마지막 배치는 Done=true.
    /// 새 검색을 시작하면 이 스트림은 Cancelled 배치로 끝난다. ct로 취소하면 서비스에 CancelSearch를 보낸다.
    /// </summary>
    public async IAsyncEnumerable<SearchBatch> SearchStreamAsync(
        string query,
        uint limit = 0,
        ulong cursor = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ActiveSearch s;
        ActiveSearch? previous;
        lock (_gate)
        {
            ThrowIfFaulted();
            previous = _search;
            s = new ActiveSearch(++_lastSearchId);
            _search = s;
        }

        previous?.Supersede();
        await WriteFrameAsync(Protocol.Encode(new SearchRequest(s.Id, query, limit, cursor)));

        var finished = false;
        try
        {
            await foreach (var b in s.Batches.Reader.ReadAllAsync(ct))
            {
                if (b.Done)
                {
                    finished = true;
                }

                yield return b;
                if (finished)
                {
                    yield break;
                }
            }

            finished = true;
        }
        finally
        {
            lock (_gate)
            {
                if (_search == s)
                {
                    _search = null;
                }
            }

            if (!finished)
            {
                await TrySendCancelAsync(s.Id);
            }
        }
    }

    /// <summary>검색 결과를 모두 모아 돌려준다 (limit 0 = 서비스 기본 1000).</summary>
    public async Task<SearchResult> SearchAsync(string query, uint limit = 0, ulong cursor = 0, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        var status = SearchStatus.Ok;
        ulong next = 0;
        await foreach (var b in SearchStreamAsync(query, limit, cursor, ct))
        {
            hits.AddRange(b.Hits);
            if (b.Done)
            {
                status = b.Status;
                next = b.NextCursor;
            }
        }

        return new SearchResult(hits, status, next);
    }

    private async Task<byte[]> RequestAsync(byte[] frame, MsgType expected, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(MsgType Type, byte[] Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeLock.WaitAsync(ct);
        try
        {
            lock (_gate)
            {
                ThrowIfFaulted();
                _pending.Enqueue(tcs);
            }

            // 프레임을 반만 쓰면 스트림이 깨지므로 쓰기는 호출자 토큰으로 끊지 않는다.
            await _pipe.WriteAsync(frame, _life.Token);
            await _pipe.FlushAsync(_life.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fault(ex);
            throw new IOException("indexer connection lost", ex);
        }
        finally
        {
            _writeLock.Release();
        }

        // 취소해도 대기열 자리는 그대로 두어 다음 응답과 짝이 어긋나지 않게 한다.
        var (type, body) = await tcs.Task.WaitAsync(ct);
        if (type == expected)
        {
            return body;
        }

        throw type == MsgType.Error
            ? new ProtocolException(Protocol.DecodeError(body).Message)
            : new ProtocolException($"unexpected reply {type}");
    }

    private async Task WriteFrameAsync(byte[] frame)
    {
        await _writeLock.WaitAsync(_life.Token);
        try
        {
            await _pipe.WriteAsync(frame, _life.Token);
            await _pipe.FlushAsync(_life.Token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fault(ex);
            throw new IOException("indexer connection lost", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task TrySendCancelAsync(uint id)
    {
        try
        {
            await WriteFrameAsync(Protocol.EncodeCancelSearch(id));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // 연결이 이미 끊겼으면 취소할 것도 없다.
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[Protocol.HeaderLength];
        try
        {
            while (true)
            {
                await _pipe.ReadExactlyAsync(header, _life.Token);
                var (len, type) = Protocol.ReadHeader(header);
                var body = new byte[len];
                await _pipe.ReadExactlyAsync(body, _life.Token);
                Dispatch(type, body);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Fault(ex);
        }
    }

    private void Dispatch(MsgType type, byte[] body)
    {
        if (type == MsgType.SearchBatch)
        {
            var b = Protocol.DecodeSearchBatch(body);
            ActiveSearch? s;
            lock (_gate)
            {
                s = _search;
            }

            // 이전(취소된) 검색의 남은 배치는 버린다.
            if (s is not null && s.Id == b.SearchId)
            {
                s.Post(b);
            }

            return;
        }

        TaskCompletionSource<(MsgType, byte[])>? t;
        ActiveSearch? active;
        lock (_gate)
        {
            _pending.TryDequeue(out t);
            active = _search;
        }

        if (t is not null)
        {
            t.TrySetResult((type, body));
        }
        else if (type == MsgType.Error)
        {
            // 짝이 없는 오류는 검색 요청 자체가 거부된 경우다.
            active?.Fail(new ProtocolException(Protocol.DecodeError(body).Message));
        }
    }

    private void Fault(Exception ex)
    {
        TaskCompletionSource<(MsgType, byte[])>[] pending;
        ActiveSearch? s;
        lock (_gate)
        {
            _fault ??= ex;
            pending = [.. _pending];
            _pending.Clear();
            s = _search;
            _search = null;
        }

        var lost = new IOException("indexer connection lost", ex);
        foreach (var p in pending)
        {
            p.TrySetException(lost);
        }

        s?.Fail(lost);
    }

    private void ThrowIfFaulted()
    {
        if (_fault is not null)
        {
            throw new IOException("indexer connection lost", _fault);
        }
    }

    private static async Task<(MsgType Type, byte[] Body)> HandshakeAsync(Stream s, byte[] frame, CancellationToken ct)
    {
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
        var header = new byte[Protocol.HeaderLength];
        await s.ReadExactlyAsync(header, ct);
        var (len, type) = Protocol.ReadHeader(header);
        var body = new byte[len];
        await s.ReadExactlyAsync(body, ct);
        return (type, body);
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        await _pipe.DisposeAsync();
        await _reader;
        _writeLock.Dispose();
        _life.Dispose();
    }

    private sealed class ActiveSearch(uint id)
    {
        public uint Id { get; } = id;

        public Channel<SearchBatch> Batches { get; } =
            Channel.CreateUnbounded<SearchBatch>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public void Post(SearchBatch b)
        {
            Batches.Writer.TryWrite(b);
            if (b.Done)
            {
                Batches.Writer.TryComplete();
            }
        }

        public void Supersede()
        {
            Batches.Writer.TryWrite(new SearchBatch(Id, [], true, SearchStatus.Cancelled, 0));
            Batches.Writer.TryComplete();
        }

        public void Fail(Exception ex) => Batches.Writer.TryComplete(ex);
    }
}
