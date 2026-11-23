using Foldy.Core.Diagnostics;
using Foldy.Core.FileSystem;

namespace Foldy.Core.Tests;

/// <summary>
/// 휴지통·삭제 도우미 프로세스 약속 (사용자 제보 2026-11-23: 큰 폴더 여러 개를 휴지통으로 보내다
/// "Abnormal program termination"으로 Foldy가 꺼짐). 도우미가 결과 없이 끝나도 Foldy는 쉬운 말로 실패만 알려야 한다.
/// </summary>
public sealed class FileOpWorkerProtocolTests
{
    [Fact]
    public void Request_round_trips_with_korean_and_long_paths()
    {
        var deep = @"C:\SEHR\project\" + string.Join('\\', Enumerable.Repeat("node_modules", 30)) + @"\한글 이름.js";
        var req = new FileOpWorkerRequest("Recycle", [@"C:\SEHR\project\KBSMC\node_modules", deep], 0x1234_5678);
        var line = FileOpWorkerProtocol.WriteRequest(req);

        Assert.DoesNotContain('\n', line);
        var back = FileOpWorkerProtocol.ReadRequest(line);
        Assert.NotNull(back);
        Assert.Equal("Recycle", back.Kind);
        Assert.Equal(req.Sources, back.Sources);
        Assert.Equal(0x1234_5678, back.Owner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"Recycle\",\"sources\":[],\"owner\":0}")]
    [InlineData("{\"kind\":\"\",\"sources\":[\"C:\\\\a\"],\"owner\":0}")]
    public void Bad_request_is_rejected(string? line) => Assert.Null(FileOpWorkerProtocol.ReadRequest(line));

    [Fact]
    public void Progress_and_result_lines_are_read_back()
    {
        FileOpWorkerProtocol.ReadLine(FileOpWorkerProtocol.WriteProgress(new FileOpWorkerProgress(5, 10, "줄\n바꿈.txt")), out var p, out var r);
        Assert.Equal(new FileOpWorkerProgress(5, 10, "줄\n바꿈.txt"), p);
        Assert.Null(r);

        FileOpWorkerProtocol.ReadLine(FileOpWorkerProtocol.WriteResult(new FileOpWorkerResult(false, false, "접근 거부")), out p, out r);
        Assert.Null(p);
        Assert.Equal(new FileOpWorkerResult(false, false, "접근 거부"), r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Abnormal program termination")]
    [InlineData("P {broken")]
    [InlineData("R ")]
    public void Unknown_or_broken_lines_are_ignored(string? line)
    {
        FileOpWorkerProtocol.ReadLine(line, out var p, out var r);
        Assert.Null(p);
        Assert.Null(r);
    }

    [Fact]
    public void Worker_that_dies_without_result_becomes_a_friendly_failure()
    {
        var r = FileOpWorkerProtocol.Finish(null, cancelRequested: false, "Windows가 지우는 도중에 멈췄어요.");
        Assert.False(r.Succeeded);
        Assert.False(r.Aborted);
        Assert.Equal("Windows가 지우는 도중에 멈췄어요.", r.Error);
    }

    [Fact]
    public void Worker_that_ends_after_cancel_is_a_cancel()
    {
        var r = FileOpWorkerProtocol.Finish(null, cancelRequested: true, "x");
        Assert.True(r.Aborted);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Worker_result_wins_when_present()
    {
        var ok = new FileOpWorkerResult(true, false, null);
        Assert.Same(ok, FileOpWorkerProtocol.Finish(ok, cancelRequested: true, "x"));
    }

    [Fact]
    public void Only_folder_deletes_go_to_the_worker()
    {
        Func<string, bool> isDir = p => p.EndsWith("node_modules", StringComparison.Ordinal);
        Assert.True(FileOpWorkerProtocol.ShouldIsolate(true, [@"C:\a\node_modules", @"C:\b\node_modules"], isDir));
        Assert.True(FileOpWorkerProtocol.ShouldIsolate(true, [@"C:\a\x.txt", @"C:\a\node_modules"], isDir));
        Assert.False(FileOpWorkerProtocol.ShouldIsolate(true, [@"C:\a\x.txt"], isDir));
        Assert.False(FileOpWorkerProtocol.ShouldIsolate(true, [], isDir));
        Assert.False(FileOpWorkerProtocol.ShouldIsolate(false, [@"C:\a\node_modules"], isDir));
    }

    [Fact]
    public void Progress_is_throttled_but_first_and_forced_always_go()
    {
        long now = 0;
        var t = new ProgressThrottle(TimeSpan.FromMilliseconds(100), () => now);
        Assert.True(t.ShouldSend());
        var sent = 0;
        for (var i = 0; i < 100_000; i++)
        {
            now = i / 100; // 100,000번 보고가 1초 동안
            if (t.ShouldSend())
            {
                sent++;
            }
        }

        Assert.InRange(sent, 9, 11);
        Assert.True(t.ShouldSend(force: true));
    }
}

public sealed class RunMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-marker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Clean_exit_leaves_nothing_for_next_run()
    {
        var m = RunMarker.Start(_dir, out var previous);
        Assert.Null(previous);
        m.Stop();

        RunMarker.Start(_dir, out previous).Stop();
        Assert.Null(previous);
    }

    [Fact]
    public void Sudden_end_during_recycle_is_reported_with_what_was_running()
    {
        RunMarker.Start(_dir, out _); // Stop 없이 꺼진 것처럼
        RunMarker.Note(@"Recycle 2: C:\SEHR\project\node_modules");

        var next = RunMarker.Start(_dir, out var previous);
        Assert.NotNull(previous);
        Assert.Contains(@"Recycle 2: C:\SEHR\project\node_modules", previous, StringComparison.Ordinal);
        next.Stop();
    }

    [Fact]
    public void Finished_activity_is_cleared()
    {
        RunMarker.Start(_dir, out _);
        RunMarker.Note("Recycle 1: C:\\x");
        RunMarker.Note(null);

        var next = RunMarker.Start(_dir, out var previous);
        Assert.NotNull(previous);
        Assert.DoesNotContain("Recycle", previous, StringComparison.Ordinal);
        next.Stop();
    }
}
