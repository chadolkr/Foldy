namespace Foldy.Indexer.Client;

/// <summary>색인 서비스와의 연결 상태.</summary>
public enum IndexerLinkState
{
    /// <summary>아직 한 번도 연결하지 못했다 (서비스 없음·설치 안 됨). 뒤에서 가끔 다시 시도한다.</summary>
    NotConnected,

    /// <summary>연결됨.</summary>
    Connected,

    /// <summary>연결돼 있다가 끊겼다 (서비스 다시 시작·업데이트 등). 곧바로, 점점 간격을 늘리며 다시 연결한다.</summary>
    Reconnecting,
}

/// <summary>
/// 색인 서비스 연결을 들고 있다가, 끊기면 스스로 다시 연결한다 (1.9.1).
/// </summary>
/// <remarks>
/// 예전에는 앱이 처음 한 번만 연결해 두고, 서비스가 다시 시작되면(업데이트·재부팅 없는 서비스 재시작·작업 관리자) 끊긴 연결을
/// 그대로 들고 있었다. 그러면 검색에서 C:(서비스 색인) 결과가 빠지고 앱을 다시 켜야만 돌아왔다 (벤치마크 중 실측: 8,076개 → 576개).
/// 이제 <see cref="IndexerClient.Disconnected"/>를 기다렸다가 끊기는 즉시 다시 연결한다. 서비스가 다시 뜰 때까지
/// 짧게 시작해 점점 길게(<see cref="DefaultBackoff"/>) 시도하고, 마지막 간격으로 계속 시도한다.
/// <see cref="StateChanged"/>는 스레드 풀에서 올라오니 UI는 자기 스레드로 넘겨서 쓴다.
/// </remarks>
public sealed class IndexerConnection : IAsyncDisposable
{
    /// <summary>끊긴 뒤 다시 연결을 시도하는 간격: 서비스 재시작은 보통 몇 초면 끝나므로 짧게 시작한다.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultBackoff =
        [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly Func<CancellationToken, Task<IndexerClient?>> _connect;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private readonly TimeSpan _idleRetry;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private IndexerClient? _current;
    private Task _loop = Task.CompletedTask;
    private bool _looping;
    private int _disposed;
    private IndexerLinkState _mode;
    private int _attempts;

    /// <param name="connect">한 번 연결해 본다 (없으면 null).</param>
    /// <param name="backoff">끊긴 뒤 다시 시도 간격. 마지막 값으로 계속 시도한다.</param>
    /// <param name="idleRetry">처음부터 연결하지 못했을 때 다시 시도하는 간격 (서비스가 나중에 설치·시작될 때).</param>
    public IndexerConnection(Func<CancellationToken, Task<IndexerClient?>> connect, IReadOnlyList<TimeSpan>? backoff = null, TimeSpan? idleRetry = null)
    {
        _connect = connect;
        _backoff = backoff is { Count: > 0 } ? backoff : DefaultBackoff;
        _idleRetry = idleRetry ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>지금 쓸 수 있는 연결 (끊겼으면 null).</summary>
    public IndexerClient? Current => Volatile.Read(ref _current) is { IsFaulted: false } c ? c : null;

    public IndexerLinkState State { get; private set; } = IndexerLinkState.NotConnected;

    /// <summary>다시 연결하려고 시도한 횟수 (시험·로그용). 연결되면 0으로 돌아간다.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>상태가 바뀔 때 (스레드 풀에서).</summary>
    public event Action<IndexerLinkState>? StateChanged;

    /// <summary>
    /// 처음 연결: 시작 직후 서비스가 바쁠 수 있어 <paramref name="tries"/>번 시도한다.
    /// 연결하지 못하면 <c>false</c>를 돌려주고, 뒤에서 <c>idleRetry</c>마다 계속 시도한다.
    /// </summary>
    public async Task<bool> StartAsync(int tries = 3, TimeSpan? between = null, CancellationToken ct = default)
    {
        for (var i = 0; i < Math.Max(1, tries); i++)
        {
            if (i > 0)
            {
                await Task.Delay(between ?? TimeSpan.FromSeconds(2), ct);
            }

            if (await TryOnceAsync(ct))
            {
                return true;
            }
        }

        StartLoop(IndexerLinkState.NotConnected);
        return false;
    }

    /// <summary>지금 연결을 버리고 다시 연결한다 (색인 서비스를 설치·제거한 뒤).</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var old = Interlocked.Exchange(ref _current, null);
        if (old is not null)
        {
            await old.DisposeAsync(); // 이미 빼 왔으니 감시 쪽은 아무것도 하지 않는다
        }

        if (!await TryOnceAsync(ct))
        {
            StartLoop(IndexerLinkState.NotConnected); // 일부러 바꾼 것(제거 등)이라 "잠깐 끊김"이 아니다
        }
    }

    private async Task<bool> TryOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Current is not null)
            {
                return true;
            }

            var c = await _connect(ct);
            if (c is null)
            {
                return false;
            }

            Adopt(c);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Adopt(IndexerClient c)
    {
        var old = Interlocked.Exchange(ref _current, c);
        if (old is not null && !ReferenceEquals(old, c))
        {
            _ = old.DisposeAsync().AsTask();
        }

        Volatile.Write(ref _attempts, 0);
        SetState(IndexerLinkState.Connected);
        _ = WatchAsync(c);
    }

    private async Task WatchAsync(IndexerClient c)
    {
        try
        {
            await c.Disconnected.WaitAsync(_life.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 닫는 중
        }

        // 이 연결이 아직 지금 연결일 때만 (ResetAsync·Adopt·닫기가 이미 빼 갔으면 그쪽이 정리한다)
        if (_life.IsCancellationRequested || !ReferenceEquals(Interlocked.CompareExchange(ref _current, null, c), c))
        {
            return;
        }

        // 끊긴 파이프 핸들을 바로 놓는다: 다시 뜨는 서비스가 같은 이름의 파이프를 처음부터(first instance) 만들 수 있게
        try
        {
            await c.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        StartLoop(IndexerLinkState.Reconnecting);
    }

    private void StartLoop(IndexerLinkState state)
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        SetState(state);
        lock (_sync)
        {
            _mode = state;
            if (_looping)
            {
                return; // 이미 시도 중 (간격만 새 상태를 따른다)
            }

            _looping = true;
            _loop = Task.Run(RetryLoopAsync, CancellationToken.None);
        }
    }

    private async Task RetryLoopAsync()
    {
        var i = 0;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                lock (_sync)
                {
                    // 연결됐으면 끝낸다. 그 직후 또 끊기면 StartLoop가 새로 시작한다 (같은 잠금 안에서 판단하므로 빠짐이 없다)
                    if (Current is not null)
                    {
                        _looping = false;
                        return;
                    }
                }

                IndexerLinkState mode;
                lock (_sync)
                {
                    mode = _mode;
                }

                var wait = mode == IndexerLinkState.Reconnecting ? _backoff[Math.Min(i, _backoff.Count - 1)] : _idleRetry;
                await Task.Delay(wait, _life.Token);
                Interlocked.Increment(ref _attempts);
                try
                {
                    await TryOnceAsync(_life.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 연결 중 뜻밖의 오류도 다음 차례에 다시 시도한다 (루프가 멈추면 다시는 연결하지 않는다)
                }

                i++;
            }
        }
        catch (OperationCanceledException)
        {
            // 닫는 중
        }

        lock (_sync)
        {
            _looping = false;
        }
    }

    private void SetState(IndexerLinkState s)
    {
        lock (_sync)
        {
            if (State == s)
            {
                return;
            }

            State = s;
        }

        StateChanged?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _life.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        if (Interlocked.Exchange(ref _current, null) is { } c)
        {
            await c.DisposeAsync();
        }

        _life.Dispose();
    }
}
