using System.Diagnostics;
using System.IO.Pipes;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>C# 클라이언트 ↔ Rust foldy-index 파이프 연결 시험 (설계서 5.6).</summary>
public class IndexerPipeTests
{
    private static readonly string ServiceExe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] GoldenFile(string name) => File.ReadAllBytes(Path.Combine(Golden, name));

    // ── 골든 프레임: Rust foldy-proto가 만든 바이트를 C#이 같은 값으로 읽고 쓰는지 ──

    [Fact]
    public void Golden_hello_matches_csharp_encoder() =>
        Protocol.Encode(new Hello(1, "Foldy 0.0.1")).Should().Equal(GoldenFile("hello.bin"));

    [Fact]
    public void Golden_hello_reply_decodes()
    {
        var f = GoldenFile("hello_reply.bin");
        var (len, type) = Protocol.ReadHeader(f);
        type.Should().Be(MsgType.HelloReply);
        len.Should().Be(f.Length - Protocol.HeaderLength);
        var r = Protocol.DecodeHelloReply(f.AsSpan(Protocol.HeaderLength));
        r.Protocol.Should().Be(1);
        r.ServiceVersion.Should().Be("0.0.1");
        r.Volumes.Should().Equal("C:", "D:");
    }

    [Fact]
    public void Golden_status_reply_decodes_volumes_and_negative_zigzag()
    {
        var f = GoldenFile("status_reply.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.StatusReply);
        var s = Protocol.DecodeStatusReply(f.AsSpan(Protocol.HeaderLength));
        s.State.Should().Be(IndexState.Ready);
        s.IndexedEntries.Should().Be(1_234_567);
        s.LastUsn.Should().Be(-42);
        s.ProgressPermille.Should().Be(1000);
        s.Volumes.Should().Equal(
            new VolumeStatus("C:", IndexState.Ready, 1_234_567, -42, 1000, ""),
            new VolumeStatus("D:", IndexState.NotReady, 0, 0, 0, "관리자 권한 필요"));
    }

    [Fact]
    public void Golden_search_requests_match_csharp_encoder()
    {
        Protocol.Encode(new SearchRequest(7, "보고서.PDF", 500, 0x0100_0000_0000_1234)).Should().Equal(GoldenFile("search.bin"));
        Protocol.EncodeCancelSearch(300).Should().Equal(GoldenFile("cancel_search.bin"));
        Protocol.EncodeFolderSizes(@"C:\Users").Should().Equal(GoldenFile("folder_sizes.bin"));
    }

    [Fact]
    public void Golden_search_batch_decodes()
    {
        var f = GoldenFile("search_batch.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.SearchBatch);
        var b = Protocol.DecodeSearchBatch(f.AsSpan(Protocol.HeaderLength));
        b.SearchId.Should().Be(7);
        b.Done.Should().BeTrue();
        b.Status.Should().Be(SearchStatus.Ok);
        b.NextCursor.Should().Be(99);
        b.Hits.Should().Equal(
            new SearchHit(@"C:\Users\me\보고서.pdf", 123_456, 126_976, 133_800_000_000_000_000, false),
            new SearchHit(@"C:\Docs", 1UL << 40, 1UL << 40, -1, true));
        b.Hits[0].LastWriteTimeUtc.Should().Be(DateTime.FromFileTimeUtc(133_800_000_000_000_000));
    }

    [Fact]
    public void Golden_folder_sizes_reply_decodes()
    {
        var f = GoldenFile("folder_sizes_reply.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.FolderSizesReply);
        var r = Protocol.DecodeFolderSizesReply(f.AsSpan(Protocol.HeaderLength));
        r.Status.Should().Be(FolderSizesStatus.Ok);
        r.TotalSize.Should().Be(5100);
        r.TotalAlloc.Should().Be(12_288);
        r.Entries.Should().Equal(new FolderEntry("a.txt", false, 100, 4096), new FolderEntry("하위", true, 5000, 8192));
    }

    [Fact]
    public void Corrupt_frames_throw_protocol_exception()
    {
        byte[] huge = [0xFF, 0xFF, 0xFF, 0x7F, 0x01, 0x80];
        FluentActions.Invoking(() => Protocol.ReadHeader(huge)).Should().Throw<ProtocolException>();
        byte[] unknown = [0, 0, 0, 0, 0x34, 0x12];
        FluentActions.Invoking(() => Protocol.ReadHeader(unknown)).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => Protocol.DecodeHelloReply([0x01, 0x10, 0x41])).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => Protocol.DecodeSearchBatch([0x01, 0x01, 0x05, 0x41])).Should().Throw<ProtocolException>();
        FluentActions.Invoking(() => Protocol.DecodeSearchBatch([0x01, 0x00, 0x07, 0x00, 0x00])).Should().Throw<ProtocolException>("bool은 0/1만");
    }

    // ── 실제 프로세스 연결 ──

    [Fact]
    public async Task No_service_falls_back_quickly()
    {
        var sw = Stopwatch.StartNew();
        var c = await IndexerClient.TryConnectAsync("Foldy.Test.Missing." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(200), Ct);
        c.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "서비스가 없으면 오래 기다리지 않고 LocalProvider로 폴백 (CI는 느려서 넉넉히)");
    }

    [Fact]
    public async Task Hello_and_status_roundtrip_with_rust_service()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--no-index");

        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        client.Should().NotBeNull();
        client!.Hello.Protocol.Should().Be(Protocol.Version);
        client.Hello.ServiceVersion.Should().NotBeNullOrEmpty();
        client.Hello.Volumes.Should().Contain("C:");

        for (var i = 0; i < 1000; i++)
        {
            (await client.StatusAsync(Ct)).State.Should().Be(IndexState.NotReady);
        }

        (await client.SearchAsync("anything", ct: Ct)).Status.Should().Be(SearchStatus.NotReady, "색인이 없으면 클라이언트가 폴백하도록");
        (await client.FolderSizesAsync(@"C:\Windows", Ct)).Status.Should().Be(FolderSizesStatus.NotIndexed);
    }

    [Fact]
    public async Task Service_survives_garbage_and_serves_next_client()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--no-index");

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

    [Fact]
    public async Task Non_elevated_service_reports_volume_not_ready()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "관리자 권한으로 실행 중이면 실제로 C: 색인을 만든다");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--volumes", "C:");
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);

        var st = await WaitForAsync(client!, s => s.Volumes.Count == 1 && s.Volumes[0].Message.Length > 0);
        st.State.Should().Be(IndexState.NotReady);
        st.Volumes[0].Volume.Should().Be("C:");
        st.Volumes[0].Message.Should().Contain("관리자");
        (await client!.SearchAsync("windows", ct: Ct)).Status.Should().Be(SearchStatus.NotReady);
    }

    [Fact]
    public async Task Search_and_folder_sizes_over_directory_index()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        using var tree = new TempTree();
        tree.File("Report-2026.PDF", 5000);
        tree.File(@"하위\report-old.pdf", 100);
        tree.File(@"하위\깊은\notes.txt", 7);
        for (var i = 0; i < 120; i++)
        {
            tree.File($"bulk-{i:000}.report", 1);
        }

        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--no-index", "--scan-dir", tree.Root);
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        client.Should().NotBeNull();
        var st = await WaitForAsync(client!, s => s.State == IndexState.Ready);
        st.IndexedEntries.Should().Be(1 + 2 + 2 + 120 + 1, "루트 포함");

        // 대소문자 무시 부분 문자열, 경로 재구성, 크기·시간
        var r = await client!.SearchAsync("REPORT-", ct: Ct);
        r.Status.Should().Be(SearchStatus.Ok);
        r.NextCursor.Should().Be(0);
        r.Hits.Select(h => h.Path).Should().BeEquivalentTo(
            Path.Combine(tree.Root, "Report-2026.PDF"), Path.Combine(tree.Root, "하위", "report-old.pdf"));
        var pdf = r.Hits.Single(h => h.Path.EndsWith("Report-2026.PDF", StringComparison.Ordinal));
        pdf.Size.Should().Be(5000);
        pdf.IsDir.Should().BeFalse();
        pdf.LastWriteTimeUtc.Should().BeCloseTo(File.GetLastWriteTimeUtc(pdf.Path), TimeSpan.FromSeconds(2));

        // limit + cursor 로 끝까지 넘기면 중복 없이 전부
        var seen = new List<string>();
        ulong cursor = 0;
        do
        {
            var page = await client.SearchAsync("report", 25, cursor, Ct);
            page.Hits.Count.Should().BeLessThanOrEqualTo(25);
            seen.AddRange(page.Hits.Select(h => h.Path));
            cursor = page.NextCursor;
        }
        while (cursor != 0);
        seen.Should().HaveCount(122).And.OnlyHaveUniqueItems();

        // 폴더 크기
        var fs = await client.FolderSizesAsync(tree.Root, Ct);
        fs.Status.Should().Be(FolderSizesStatus.Ok);
        fs.TotalSize.Should().Be(5000 + 100 + 7 + 120);
        fs.Entries.Single(e => e.Name == "하위").Should().Be(new FolderEntry("하위", true, 107, 8192));
        (await client.FolderSizesAsync(Path.Combine(tree.Root, "없음"), Ct)).Status.Should().Be(FolderSizesStatus.NotFound);

        // 검색 중간에 Status 같은 다른 요청을 섞어도 짝이 맞는다
        var statusTask = client.StatusAsync(Ct);
        var again = await client.SearchAsync("bulk-", ct: Ct);
        (await statusTask).State.Should().Be(IndexState.Ready);
        again.Hits.Should().HaveCount(120);
    }

    [Fact]
    public async Task New_search_supersedes_previous_and_cancel_keeps_connection_usable()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        using var tree = new TempTree();
        for (var i = 0; i < 3000; i++)
        {
            tree.File($"item-{i:0000}.dat", 1);
        }

        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--no-index", "--scan-dir", tree.Root);
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        await WaitForAsync(client!, s => s.State == IndexState.Ready);

        // 첫 검색의 첫 배치만 받고 두 번째 검색을 시작하면 첫 스트림은 Cancelled로 끝난다.
        await using var first = client!.SearchStreamAsync("item", 3000, ct: Ct).GetAsyncEnumerator(Ct);
        (await first.MoveNextAsync()).Should().BeTrue();
        var second = await client.SearchAsync("item-0042", ct: Ct);
        second.Hits.Should().ContainSingle().Which.Path.Should().EndWith("item-0042.dat");
        var last = first.Current;
        while (!last.Done && await first.MoveNextAsync())
        {
            last = first.Current;
        }

        last.Done.Should().BeTrue();

        // 호출자 취소 → CancelSearch 전송 후에도 연결은 계속 쓸 수 있다.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => client.SearchAsync("item", ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        (await client.SearchAsync("item-2999", ct: Ct)).Hits.Should().ContainSingle();
        (await client.StatusAsync(Ct)).State.Should().Be(IndexState.Ready);
        client.IsFaulted.Should().BeFalse();
    }

    private static string NewPipeName() => "Foldy.Test." + Guid.NewGuid().ToString("N");

    private static async Task<StatusReply> WaitForAsync(IndexerClient client, Func<StatusReply, bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var s = await client.StatusAsync(Ct);
            if (done(s) || sw.Elapsed > TimeSpan.FromSeconds(15))
            {
                return s;
            }

            await Task.Delay(50, Ct);
        }
    }

    private static async Task<ServiceProcess> StartServiceAsync(string pipe, params string[] extra)
    {
        var p = Process.Start(new ProcessStartInfo(ServiceExe, ["--pipe", pipe, .. extra])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
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
                Process.WaitForExit(5000);
            }

            Process.Dispose();
        }
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "foldy-idx-" + Guid.NewGuid().ToString("N"))).FullName;

        public void File(string relative, int size)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllBytes(full, new byte[size]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 서비스가 아직 핸들을 쥐고 있으면 임시 폴더는 남겨 둔다.
            }
        }
    }
}
