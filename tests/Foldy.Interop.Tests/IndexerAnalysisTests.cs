using System.Diagnostics;
using Foldy.Analysis;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>
/// 디스크 분석 파이프 메시지 (BuildAnalysis / AnalysisInfo, StatusReply.analysis) 골든 파일과,
/// Rust foldy-index가 만든 분석 DB를 C# Foldy.Analysis가 읽기 전용으로 여는 끝까지 시험 (설계서 7.3).
/// </summary>
public class IndexerAnalysisTests
{
    private static readonly string ServiceExe = Path.Combine(AppContext.BaseDirectory, "foldy-index.exe");
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] GoldenFile(string name) => File.ReadAllBytes(Path.Combine(Golden, name));

    [Fact]
    public void Golden_analysis_requests_match_csharp_encoder()
    {
        Protocol.EncodeBuildAnalysis("C:").Should().Equal(GoldenFile("build_analysis.bin"));
        Protocol.EncodeAnalysisInfo(@"D:\작업").Should().Equal(GoldenFile("analysis_info.bin"));
    }

    [Fact]
    public void Golden_analysis_replies_decode()
    {
        var f = GoldenFile("build_analysis_reply.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.BuildAnalysisReply);
        Protocol.DecodeBuildAnalysisReply(f.AsSpan(Protocol.HeaderLength)).Should().Be(new BuildAnalysisReply(AnalysisStatus.AlreadyRunning, "이미 분석 중"));

        f = GoldenFile("analysis_info_reply.bin");
        Protocol.ReadHeader(f).Type.Should().Be(MsgType.AnalysisInfoReply);
        var r = Protocol.DecodeAnalysisInfoReply(f.AsSpan(Protocol.HeaderLength));
        r.Status.Should().Be(AnalysisStatus.Ok);
        r.Volume.Should().Be("C:");
        r.DbPath.Should().EndWith(".20261119T010203Z.db");
        r.ScanTime.Should().Be(134_080_000_000_000_000);
        r.Previous.Should().ContainSingle().Which.ScanTime.Should().Be(134_074_000_000_000_000);

        // StatusReply 끝에 붙은 analysis 목록
        f = GoldenFile("status_reply.bin");
        var s = Protocol.DecodeStatusReply(f.AsSpan(Protocol.HeaderLength));
        s.Analysis.Should().Equal(new AnalysisProgressStatus("C:", AnalysisJobState.Building, 420, "파일 쓰는 중"));
        // 옛 서비스(analysis 목록 없음)도 읽는다: 목록 길이(1) + 볼륨(1+2) + 상태(1) + 진행률 420(varint 2) + 메시지(1+n)를 잘라 낸다
        var old = f.AsSpan(Protocol.HeaderLength).ToArray();
        var cut = old.Length - (1 + 1 + "C:".Length + 1 + 2 + 1 + System.Text.Encoding.UTF8.GetByteCount("파일 쓰는 중"));
        Protocol.DecodeStatusReply(old.AsSpan(0, cut)).Analysis.Should().BeEmpty();
    }

    [Fact]
    public async Task Rust_service_builds_snapshot_that_csharp_reads()
    {
        NativeRequirement.Ensure(File.Exists(ServiceExe), "foldy-index.exe");
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "foldy-an-idx-" + Guid.NewGuid().ToString("N"))).FullName;
        var outDir = Path.Combine(Path.GetTempPath(), "foldy-an-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = DateTime.UtcNow.AddDays(-60);
            Write(Path.Combine(root, "web", "package.json"), 10, old);
            for (var i = 0; i < 11; i++)
            {
                Write(Path.Combine(root, "web", "node_modules", $"p{i}", "index.js"), 1 << 20, old);
            }

            Write(Path.Combine(root, "docs", "보고서.pdf"), 5000, null);

            var pipe = "Foldy.Test." + Guid.NewGuid().ToString("N");
            using var p = Process.Start(new ProcessStartInfo(ServiceExe, ["--pipe", pipe, "--no-index", "--scan-dir", root, "--analysis-dir", outDir])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            try
            {
                (await p.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().StartWith("READY");
                await using var client = await IndexerClient.TryConnectAsync(pipe, TimeSpan.FromSeconds(5), Ct);
                client.Should().NotBeNull();
                var sw = Stopwatch.StartNew();
                while ((await client!.StatusAsync(Ct)).State != IndexState.Ready && sw.Elapsed < TimeSpan.FromSeconds(15))
                {
                    await Task.Delay(50, Ct);
                }

                (await client.AnalysisInfoAsync(root, Ct)).Status.Should().Be(AnalysisStatus.NotFound);
                (await client.BuildAnalysisAsync(@"Q:\없음", Ct)).Status.Should().Be(AnalysisStatus.UnknownVolume);

                // 서비스 어댑터로 만들고 끝날 때까지 기다린다 (같은 연결 재사용)
                var svc = new IndexerAnalysisService(_ => Task.FromResult<IndexerClient?>(client), disposeClient: false) { PollInterval = TimeSpan.FromMilliseconds(50) };
                var progress = new List<AnalysisProgress>();
                var set = await svc.BuildAsync(root, new Sync(progress), Ct);
                set.Should().NotBeNull();
                set!.FromService.Should().BeTrue();
                File.Exists(set.Latest.DbPath).Should().BeTrue();
                Path.GetDirectoryName(set.Latest.DbPath).Should().Be(outDir);
                progress.Should().NotBeEmpty();
                (await client.StatusAsync(Ct)).Analysis.Should().ContainSingle().Which.State.Should().Be(AnalysisJobState.Ready);

                // Rust가 만든 DB를 C#이 mode=ro로 연다
                using (var db = AnalysisDb.Open(set.Latest.DbPath))
                {
                    db.Scan().TotalFiles.Should().Be(13);
                    db.PathOf(db.RootId).Should().Be(root);
                    var nm = db.FindDir(Path.Combine(root, "web", "node_modules"))!.Value;
                    db.Folder(nm)!.Tag.Should().Be("node_modules");
                    db.Folder(nm)!.TotalFiles.Should().Be(11);
                    var c = db.Candidates().Should().ContainSingle().Subject;
                    c.Rule.Should().Be("node-modules");
                    c.Path.Should().Be(Path.Combine(root, "web", "node_modules"));
                    db.TopFiles(1)[0].Size.Should().Be(1 << 20);
                    db.ExtensionStats().Single(e => e.Ext == "pdf").Files.Should().Be(1);
                }

                // 두 번째 분석: 이전 스냅샷이 Previous로 온다
                await Task.Delay(1100, Ct);
                var again = await svc.BuildAsync(root, null, Ct);
                again!.Previous.Should().ContainSingle().Which.DbPath.Should().Be(set.Latest.DbPath);
                (await svc.GetLatestAsync(root, Ct))!.Latest.DbPath.Should().Be(again.Latest.DbPath);
            }
            finally
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(outDir);
        }
    }

    private static void Write(string path, int size, DateTime? mtime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        if (mtime is { } t)
        {
            File.SetLastWriteTimeUtc(path, t);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Sync(List<AnalysisProgress> sink) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
