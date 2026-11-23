using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>
/// 색인 서비스가 다시 시작되면 앱이 스스로 다시 연결한다 (1.9.1, 벤치마크 중 발견: 서비스 재시작 뒤 앱을 다시 켤 때까지
/// C: 결과가 빠져 readme 검색이 8,076개 대신 576개).
/// </summary>
public class IndexerReconnectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan[] FastBackoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)];

    [Fact]
    public async Task Service_restart_reconnects_and_search_works_again()
    {
        NativeRequirement.Ensure(File.Exists(IndexerPipeTests.ServiceExe), "foldy-index.exe");
        using var tree = new IndexerPipeTests.TempTree();
        for (var i = 0; i < 30; i++)
        {
            tree.File($@"p{i}\readme.md", 10);
        }

        var pipe = IndexerPipeTests.NewPipeName();
        var svc = await IndexerPipeTests.StartServiceAsync(pipe, "--no-index", "--scan-dir", tree.Root);
        try
        {
            var states = new ConcurrentQueue<IndexerLinkState>();
            await using var link = new IndexerConnection(ct => IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(2), ct), FastBackoff);
            link.StateChanged += states.Enqueue;
            (await link.StartAsync(ct: Ct)).Should().BeTrue();
            await IndexerPipeTests.WaitForAsync(link.Current!, s => s.State == IndexState.Ready);
            (await link.Current!.SearchAsync("readme", ct: Ct)).Hits.Should().HaveCount(30);

            // 서비스가 멈췄다가(재시작·업데이트) 다시 뜬다
            svc.Dispose();
            await WaitUntilAsync(() => link.State == IndexerLinkState.Reconnecting);
            link.Current.Should().BeNull("끊긴 연결을 계속 들고 있으면 안 된다");

            svc = await IndexerPipeTests.StartServiceAsync(pipe, "--no-index", "--scan-dir", tree.Root);
            await WaitUntilAsync(() => link.State == IndexerLinkState.Connected && link.Current is not null);
            await IndexerPipeTests.WaitForAsync(link.Current!, s => s.State == IndexState.Ready);
            (await link.Current!.SearchAsync("readme", ct: Ct)).Hits.Should().HaveCount(30, "다시 연결된 뒤 결과가 모두 돌아온다");
            states.Should().Equal(IndexerLinkState.Connected, IndexerLinkState.Reconnecting, IndexerLinkState.Connected);
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Dropped_connection_is_noticed_and_reconnected_with_backoff()
    {
        var name = IndexerPipeTests.NewPipeName();
        await using var server = new FakeIndexer(name);
        server.Start();
        var states = new ConcurrentQueue<IndexerLinkState>();
        await using var link = new IndexerConnection(ct => IndexerClient.TryConnectAsync(name, TimeSpan.FromSeconds(2), ct), FastBackoff);
        link.StateChanged += states.Enqueue;
        (await link.StartAsync(ct: Ct)).Should().BeTrue();
        var first = link.Current;
        first.Should().NotBeNull();

        // 서비스가 멈춤: 끊김을 바로 알아채고 다시 시도를 거듭한다
        await server.StopAsync();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Reconnecting);
        first!.IsFaulted.Should().BeTrue();
        first.Disconnected.IsCompleted.Should().BeTrue();
        link.Current.Should().BeNull();
        await WaitUntilAsync(() => link.Attempts >= 3);

        // 다시 뜸: 다음 시도에서 연결
        server.Start();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Connected && link.Current is not null);
        link.Current.Should().NotBeSameAs(first);
        link.Attempts.Should().Be(0);
        states.Should().Equal(IndexerLinkState.Connected, IndexerLinkState.Reconnecting, IndexerLinkState.Connected);

        // 한 번 더 끊겨도 마찬가지
        await server.StopAsync();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Reconnecting);
        server.Start();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Connected && link.Current is not null);
    }

    [Fact]
    public async Task Service_that_starts_later_is_picked_up()
    {
        var name = IndexerPipeTests.NewPipeName();
        await using var server = new FakeIndexer(name);
        await using var link = new IndexerConnection(ct => IndexerClient.TryConnectAsync(name, TimeSpan.FromMilliseconds(200), ct), FastBackoff, idleRetry: TimeSpan.FromMilliseconds(100));
        (await link.StartAsync(tries: 1, ct: Ct)).Should().BeFalse();
        link.State.Should().Be(IndexerLinkState.NotConnected);

        server.Start();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Connected && link.Current is not null);
    }

    [Fact]
    public async Task Closing_stops_reconnecting()
    {
        var name = IndexerPipeTests.NewPipeName();
        await using var server = new FakeIndexer(name);
        server.Start();
        await using var link = new IndexerConnection(ct => IndexerClient.TryConnectAsync(name, TimeSpan.FromSeconds(2), ct), FastBackoff);
        (await link.StartAsync(ct: Ct)).Should().BeTrue();
        await server.StopAsync();
        await WaitUntilAsync(() => link.State == IndexerLinkState.Reconnecting);

        await link.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var tries = link.Attempts;
        server.Start();
        await Task.Delay(500, Ct);
        link.Attempts.Should().Be(tries, "닫은 뒤에는 더 시도하지 않는다");
        link.Current.Should().BeNull();
    }

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "CI는 느려서 넉넉히");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Hello에만 답하는 가짜 색인 서비스. 멈추면 열린 연결을 모두 끊는다 (서비스 재시작 흉내).</summary>
    private sealed class FakeIndexer(string name) : IAsyncDisposable
    {
        private readonly ConcurrentBag<NamedPipeServerStream> _open = [];
        private CancellationTokenSource? _cts;
        private Task _loop = Task.CompletedTask;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => ServeAsync(ct), CancellationToken.None);
        }

        public async Task StopAsync()
        {
            if (_cts is null)
            {
                return;
            }

            await _cts.CancelAsync();
            await _loop;
            while (_open.TryTake(out var s))
            {
                await s.DisposeAsync();
            }

            _cts.Dispose();
            _cts = null;
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var s = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await s.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await s.DisposeAsync();
                    return;
                }

                _open.Add(s);
                _ = AnswerHelloAsync(s, ct);
            }
        }

        private static async Task AnswerHelloAsync(NamedPipeServerStream s, CancellationToken ct)
        {
            try
            {
                var header = new byte[Protocol.HeaderLength];
                await s.ReadExactlyAsync(header, ct);
                var (len, _) = Protocol.ReadHeader(header);
                await s.ReadExactlyAsync(new byte[len], ct);

                // HelloReply (postcard): protocol, service_version, volumes[]
                var version = Encoding.UTF8.GetBytes("fake");
                byte[] body = [(byte)Protocol.Version, (byte)version.Length, .. version, 0];
                await s.WriteAsync(Protocol.Frame(MsgType.HelloReply, body), ct);
                await s.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // 멈추는 중
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
