using System.Diagnostics;
using System.IO.Pipes;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>C# 클라이언트 ↔ Rust foldy-index 파이프 연결 시험 (설계서 5.6, M0).</summary>
public class IndexerPipeTests
{
    private static readonly string ServiceExe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── 골든 프레임: Rust foldy-proto가 만든 바이트를 C#이 같은 값으로 읽고 쓰는지 ──

    [Fact]
    public void Golden_hello_matches_csharp_encoder() =>
        Protocol.Encode(new Hello(1, "Foldy 0.0.1")).Should().Equal(File.ReadAllBytes(Path.Combine(Golden, "hello.bin")));

    [Fact]
    public void Golden_hello_reply_decodes()
    {
        var f = File.ReadAllBytes(Path.Combine(Golden, "hello_reply.bin"));
        var (len, type) = Protocol.ReadHeader(f);
        type.Should().Be(MsgType.HelloReply);
        len.Should().Be(f.Length - Protocol.HeaderLength);
        var r = Protocol.DecodeHelloReply(f.AsSpan(Protocol.HeaderLength));
        r.Protocol.Should().Be(1);
        r.ServiceVersion.Should().Be("0.0.1");
        r.Volumes.Should().Equal("C:", "D:");
    }

    [Fact]
    public void Golden_status_reply_decodes_negative_zigzag()
    {
        var f = File.ReadAllBytes(Path.Combine(Golden, "status_reply.bin"));
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.StatusReply);
        Protocol.DecodeStatusReply(f.AsSpan(Protocol.HeaderLength))
            .Should().Be(new StatusReply(2, 1_234_567, -42));
    }

    [Fact]
    public void Corrupt_frames_throw_protocol_exception()
    {
        byte[] huge = [0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x80];
        FluentActions.Invoking(() => Protocol.ReadHeader(huge)).Should().Throw<ProtocolException>();
        byte[] unknown = [0, 0, 0, 0, 0x34, 0x12];
        FluentActions.Invoking(() => Protocol.ReadHeader(unknown)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => Protocol.DecodeHelloReply([0x01, 0x10, 0x41])).Should().Throw<ProtocolException>();
    }

    // ── 실제 프로세스 연결 ──

    [Fact]
    public async Task No_service_falls_back_quickly()
    {
        var sw = Stopwatch.StartNew();
        var c = await IndexerClient.TryConnectAsync("Foldy.Test.Missing." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200), Ct);
        c.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "서비스가 없으면 즉시 LocalProvider로 폴백");
    }

    [Fact]
    public async Task Hello_and_status_roundtrip_with_rust_service()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var pipe = "Foldy.Test." + Guid.NewGuid().ToString("N");
        using var svc = await StartServiceAsync(pipe);

        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        client.Should().NotBeNull();
        client!.Hello.Protocol.Should().Be(Protocol.Version);
        client.Hello.ServiceVersion.Should().NotBeNullOrEmpty();
        client.Hello.Volumes.Should().Contain("C:");

        for (var i = 0; i < 1000; i++)
        {
            (await client.StatusAsync(Ct)).State.Should().Be(0);
        }
    }

    [Fact]
    public async Task Service_survives_garbage_and_serves_next_client()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var pipe = "Foldy.Test." + Guid.NewGuid().ToString("N");
        using var svc = await StartServiceAsync(pipe);

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000, Ct);
            await raw.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x00 }, Ct); // 너무 큰 길이
            var header = new byte[Protocol.HeaderLength];
            await raw.ReadExactlyAsync(header, Ct);
            Protocol.ReadHeader(header).Type.Should().Be(MsgType.Error);
        }

        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        client.Should().NotBeNull("잘못된 클라이언트가 있어도 서비스는 계속 동작");
        svc.Process.HasExited.Should().BeFalse();
    }

    private static async Task<ServiceProcess> StartServiceAsync(string pipe)
    {
        var p = Process.Start(new ProcessStartInfo(ServiceExe, ["--pipe", pipe])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var ready = await p.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        ready.Should().StartWith("READY");
        return new ServiceProcess(p);
    }

    private sealed class ServiceProcess(Process p) : IDisposable
    {
        public Process Process { get; } = p;

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
            }

            Process.Dispose();
        }
    }
}
