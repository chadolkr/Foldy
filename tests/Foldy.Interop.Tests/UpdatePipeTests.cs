using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>
/// 서비스 자동 업데이트 파이프 메시지 (설계서 10.5): 골든 프레임 + 실제 foldy-index 프로세스와 가짜 릴리스 서버.
/// 서비스는 <c>--update-fake-install</c>로 msiexec 대신 실행할 명령을 파일에 적기만 한다 (실제 설치 없음).
/// </summary>
public class UpdatePipeTests
{
    private static readonly string ServiceExe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden");
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "update-fixtures");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] GoldenFile(string name) => File.ReadAllBytes(Path.Combine(Golden, name));

    [Fact]
    public void Golden_update_requests_match_csharp_encoder()
    {
        Protocol.EncodeUpdateStatus().Should().Equal(GoldenFile("update_status.bin"));
        Protocol.EncodeCheckNow().Should().Equal(GoldenFile("check_now.bin"));
        Protocol.EncodeInstallWhenIdle(true, 300).Should().Equal(GoldenFile("install_when_idle.bin"));
        Protocol.EncodeSkipVersion("1.3.0-beta.2").Should().Equal(GoldenFile("skip_version.bin"));
        Protocol.EncodeSetChannel(UpdateChannel.Beta).Should().Equal(GoldenFile("set_channel.bin"));
        Protocol.EncodeAppStarted("1.2.3").Should().Equal(GoldenFile("app_started.bin"));
    }

    [Fact]
    public void Golden_update_status_reply_decodes()
    {
        var f = GoldenFile("update_status_reply.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.UpdateStatusReply);
        var s = Protocol.DecodeUpdateStatusReply(f.AsSpan(Protocol.HeaderLength));
        s.Should().Be(new UpdateStatusReply(
            UpdateState.Downloading, "1.2.2", "1.2.3", UpdateChannel.Stable, 512, 150_000, 300_000, 1_795_000_000, "네트워크 오류", true, false, "변경 내역"));
        s.LastCheck.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_795_000_000));
    }

    [Fact]
    public async Task Service_without_updater_reports_disabled()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(pipe, "--no-index");
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        var ex = await FluentActions.Awaiting(() => client!.UpdateStatusAsync(Ct)).Should().ThrowAsync<ProtocolException>();
        ex.Which.ErrorCode.Should().Be(UpdateErrorCodes.Disabled, "업데이트가 꺼진 서비스면 앱은 기존(UAC) 방식으로 폴백");
        (await client!.StatusAsync(Ct)).State.Should().Be(IndexState.NotReady, "연결은 계속 쓸 수 있다");
    }

    [Fact]
    public async Task Check_download_verify_and_install_without_uac()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        using var work = new TempDir();
        using var srv = new FakeReleaseServer();
        srv.Route("/dl/update.json", File.ReadAllBytes(Path.Combine(Fixtures, "update.json")));
        srv.Route("/dl/update.json.sig", File.ReadAllBytes(Path.Combine(Fixtures, "update.json.sig")));
        srv.Route("/dl/Foldy-1.2.3-x64.msi", Payload());
        srv.Route("/repos/chadolkr/Foldy/releases/latest", Encoding.UTF8.GetBytes(
            $$"""
            {"tag_name":"v1.2.3","prerelease":false,"draft":false,"body":"변경 내역","html_url":"","assets":[
              {"name":"update.json","size":1,"browser_download_url":"{{srv.Base}}/dl/update.json"},
              {"name":"update.json.sig","size":1,"browser_download_url":"{{srv.Base}}/dl/update.json.sig"},
              {"name":"Foldy-1.2.3-x64.msi","size":300000,"browser_download_url":"{{srv.Base}}/dl/Foldy-1.2.3-x64.msi"}]}
            """));

        var updates = Path.Combine(work.Root, "Updates");
        var log = Path.Combine(work.Root, "fake-install.log");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(
            pipe,
            "--no-index",
            "--update-api", srv.Base,
            "--update-dir", updates,
            "--update-pubkey", Path.Combine(Fixtures, "test-signing.pub"),
            "--update-current", "1.0.0",
            "--update-install-dir", work.Root,
            "--update-fake-install", log);
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        client.Should().NotBeNull();

        var s = await client!.UpdateStatusAsync(Ct);
        s.State.Should().Be(UpdateState.Idle);
        s.CurrentVersion.Should().Be("1.0.0");
        s.Channel.Should().Be(UpdateChannel.Stable);

        // 준비된 것이 없으면 설치 요청은 거부
        var notReady = await FluentActions.Awaiting(() => client.InstallUpdateWhenIdleAsync(true, 1, Ct)).Should().ThrowAsync<ProtocolException>();
        notReady.Which.ErrorCode.Should().Be(UpdateErrorCodes.NotReady);

        await client.CheckForUpdateAsync(Ct);
        s = await WaitForAsync(client, x => x.State is UpdateState.ReadyToInstall or UpdateState.Failed);
        s.State.Should().Be(UpdateState.ReadyToInstall, s.LastError);
        s.AvailableVersion.Should().Be("1.2.3");
        s.Notes.Should().Be("변경 내역");
        (s.Downloaded, s.Total, s.ProgressPermille).Should().Be((300_000UL, 300_000UL, (ushort)1000));
        s.LastCheck.Should().NotBeNull();
        var msi = Path.Combine(updates, "Foldy-1.2.3-x64.msi");
        File.ReadAllBytes(msi).Should().Equal(Payload(), "서명된 update.json의 SHA-256과 같은 파일");

        uint session;
        using (var me = Process.GetCurrentProcess())
        {
            session = (uint)me.SessionId;
        }

        s = await client.InstallUpdateWhenIdleAsync(relaunch: true, session, Ct);
        (s.InstallPending || s.State is UpdateState.Installing or UpdateState.Idle).Should().BeTrue();
        var lines = await WaitForLinesAsync(log, 2);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("install ");
        lines[0].ToUpperInvariant().Should().Contain((@"system32\msiexec.exe /i " + msi + " /qn /norestart").ToUpperInvariant(), "검증된 MSI를 경로 그대로 (\\\\?\\ 없이) 조용히 설치");
        lines[0].Should().NotContain("FOLDY_ROLLBACK");
        lines[1].Should().Be($"relaunch {Path.Combine(work.Root, "Foldy.exe")} session={session}");

        s = await WaitForAsync(client, x => x.State == UpdateState.Idle);
        s.AvailableVersion.Should().BeEmpty();
        (await client.ReportAppStartedAsync("1.0.0", Ct)).State.Should().Be(UpdateState.Idle);
        (await client.SetUpdateChannelAsync(UpdateChannel.Beta, Ct)).Channel.Should().Be(UpdateChannel.Beta);
        var bad = await FluentActions.Awaiting(() => client.SkipUpdateVersionAsync("not-a-version", Ct)).Should().ThrowAsync<ProtocolException>();
        bad.Which.ErrorCode.Should().Be(UpdateErrorCodes.BadArgument);
        client.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Tampered_manifest_is_never_installed()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        using var work = new TempDir();
        using var srv = new FakeReleaseServer();
        srv.Route("/dl/update.json", File.ReadAllBytes(Path.Combine(Fixtures, "update.tampered.json")));
        srv.Route("/dl/update.json.sig", File.ReadAllBytes(Path.Combine(Fixtures, "update.json.sig")));
        srv.Route("/dl/Foldy-1.2.3-x64.msi", Payload());
        srv.Route("/repos/chadolkr/Foldy/releases/latest", Encoding.UTF8.GetBytes(
            $$"""
            {"tag_name":"v1.2.3","prerelease":false,"draft":false,"body":"","html_url":"","assets":[
              {"name":"update.json","size":1,"browser_download_url":"{{srv.Base}}/dl/update.json"},
              {"name":"update.json.sig","size":1,"browser_download_url":"{{srv.Base}}/dl/update.json.sig"},
              {"name":"Foldy-1.2.3-x64.msi","size":300000,"browser_download_url":"{{srv.Base}}/dl/Foldy-1.2.3-x64.msi"}]}
            """));
        var log = Path.Combine(work.Root, "fake-install.log");
        var pipe = NewPipeName();
        using var svc = await StartServiceAsync(
            pipe, "--no-index", "--update-api", srv.Base, "--update-dir", Path.Combine(work.Root, "Updates"),
            "--update-pubkey", Path.Combine(Fixtures, "test-signing.pub"), "--update-current", "1.0.0", "--update-fake-install", log);
        await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
        await client!.CheckForUpdateAsync(Ct);
        var s = await WaitForAsync(client, x => x.State is UpdateState.ReadyToInstall or UpdateState.Failed);
        s.State.Should().Be(UpdateState.Failed);
        s.LastError.Should().Contain("서명");
        await FluentActions.Awaiting(() => client.InstallUpdateWhenIdleAsync(false, 1, Ct)).Should().ThrowAsync<ProtocolException>();
        File.Exists(log).Should().BeFalse("검증되지 않은 파일로는 아무것도 실행하지 않는다");
        File.Exists(Path.Combine(work.Root, "Updates", "Foldy-1.2.3-x64.msi")).Should().BeFalse();
    }

    /// <summary>foldy-update 픽스처와 같은 설치 파일 내용 (300,000바이트 패턴).</summary>
    private static byte[] Payload()
    {
        var b = new byte[300_000];
        for (var i = 0; i < b.Length; i++)
        {
            b[i] = (byte)(((i * 31) + 7) % 251);
        }

        return b;
    }

    private static string NewPipeName() => "Foldy.Test." + Guid.NewGuid().ToString("N");

    private static async Task<UpdateStatusReply> WaitForAsync(IndexerClient client, Func<UpdateStatusReply, bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var s = await client.UpdateStatusAsync(Ct);
            if (done(s) || sw.Elapsed > TimeSpan.FromSeconds(60)) // CI는 느리다 (설치 + 재실행 지연 5초)
            {
                return s;
            }

            await Task.Delay(50, Ct);
        }
    }

    private static async Task<string[]> WaitForLinesAsync(string path, int count)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var lines = ReadShared(path);
            if (lines.Length >= count || sw.Elapsed > TimeSpan.FromSeconds(60)) // CI는 느리다 (설치 + 재실행 지연 5초)
            {
                return lines;
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// 서비스가 덧붙여 쓰는 중에도 읽는다 (쓰기 공유 허용). 열고 있는 순간과 겹치면 다음 차례에 다시 (가끔 실패하던 원인).
    /// </summary>
    private static string[] ReadShared(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            var text = r.ReadToEnd();
            // 줄이 다 써지기 전(줄바꿈 전)의 마지막 조각은 아직 세지 않는다
            var complete = text.EndsWith('\n') ? text : text[..(text.LastIndexOf('\n') + 1)];
            return complete.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return [];
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
        public void Dispose()
        {
            if (!p.HasExited)
            {
                p.Kill();
                p.WaitForExit(5000);
            }

            p.Dispose();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "foldy-upd-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 서비스가 아직 파일을 쥐고 있으면 남겨 둔다.
            }
        }
    }

    /// <summary>GitHub Releases 응답을 흉내 내는 루프백 HTTP 서버 (GET만, 연결마다 닫음).</summary>
    private sealed class FakeReleaseServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, byte[]> _routes = [];
        private readonly CancellationTokenSource _stop = new();

        public FakeReleaseServer()
        {
            _listener.Start();
            Base = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Base { get; }

        public void Route(string path, byte[] body)
        {
            lock (_routes)
            {
                _routes[path] = body;
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient c;
                try
                {
                    c = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(c));
            }
        }

        private async Task ServeAsync(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var stream = c.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync() ?? "";
                    string? header;
                    long? rangeStart = null;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                    {
                        if (header.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase) && long.TryParse(header[13..].TrimEnd('-'), out var r))
                        {
                            rangeStart = r;
                        }
                    }

                    var path = requestLine.Split(' ') is [_, var p, ..] ? p : "/";
                    byte[]? body;
                    lock (_routes)
                    {
                        _routes.TryGetValue(path, out body);
                    }

                    string head;
                    if (body is null)
                    {
                        head = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                        body = [];
                    }
                    else if (rangeStart is { } start && start < body.Length)
                    {
                        head = $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{body.Length - 1}/{body.Length}\r\nContent-Length: {body.Length - start}\r\nConnection: close\r\n\r\n";
                        body = body[(int)start..];
                    }
                    else
                    {
                        head = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    // 클라이언트가 먼저 끊음
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
