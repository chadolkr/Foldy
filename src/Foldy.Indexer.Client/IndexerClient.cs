using System.IO.Pipes;

namespace Foldy.Indexer.Client;

/// <summary>
/// 색인 서비스 클라이언트. 연결·버전 확인에 실패하면 <see cref="TryConnectAsync"/>가 null을 반환하고,
/// 호출자는 LocalProvider로 폴백한다 (설계서 5.6, 5.8). 요청은 한 번에 하나씩 직렬화된다.
/// </summary>
public sealed class IndexerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private IndexerClient(NamedPipeClientStream pipe, HelloReply hello)
    {
        _pipe = pipe;
        Hello = hello;
    }

    public HelloReply Hello { get; }

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

            var (type, body) = await RoundTripAsync(
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

            return new IndexerClient(pipe, hello);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (type, body) = await RoundTripAsync(_pipe, Protocol.EncodeStatus(), ct);
            return type switch
            {
                MsgType.StatusReply => Protocol.DecodeStatusReply(body),
                MsgType.Error => throw new ProtocolException(Protocol.DecodeError(body).Message),
                _ => throw new ProtocolException($"unexpected reply {type}"),
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<(MsgType Type, byte[] Body)> RoundTripAsync(Stream s, byte[] frame, CancellationToken ct)
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
        await _pipe.DisposeAsync();
        _lock.Dispose();
    }
}
