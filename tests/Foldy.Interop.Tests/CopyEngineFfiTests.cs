using System.Diagnostics;
using Foldy.Copy.Native;

namespace Foldy.Interop.Tests;

/// <summary>M0 관문: "FFI 안정성 확인" (설계서 11장).</summary>
public sealed class CopyEngineFfiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-ffi-" + Guid.NewGuid().ToString("N"));

    public CopyEngineFfiTests()
    {
        Directory.CreateDirectory(_dir);
        NativeRequirement.Ensure(NativeCopyEngine.IsAvailable, "foldy_copy.dll (" + NativeCopyEngine.UnavailableReason + ")");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static NativeProgress WaitDone(NativeCopyJob job, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        NativeProgress p;
        while (!(p = job.Poll()).IsTerminal)
        {
            if (sw.Elapsed > (timeout ?? TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException(p.ToString());
            }

            Thread.Sleep(5);
        }

        return p;
    }

    [Fact]
    public void Copies_files_with_korean_and_long_names()
    {
        var src = Path.Combine(_dir, "원본 폴더");
        Directory.CreateDirectory(src);
        var a = Path.Combine(src, "한글 파일 이름 😀.txt");
        var b = Path.Combine(src, new string('x', 200) + ".bin");
        File.WriteAllText(a, "안녕하세요");
        File.WriteAllBytes(b, new byte[5 * 1024 * 1024 + 3]);

        using var job = NativeCopyEngine.CreateJob([a, b], Path.Combine(_dir, "대상"));
        job.Start();
        var p = WaitDone(job);

        p.State.Should().Be(NativeJobState.Done);
        p.FilesDone.Should().Be(2);
        p.BytesDone.Should().Be(p.BytesTotal);
        File.ReadAllText(Path.Combine(_dir, "대상", "한글 파일 이름 😀.txt")).Should().Be("안녕하세요");
    }

    [Fact]
    public void Panic_inside_engine_returns_error_code_and_process_survives()
    {
        for (var i = 0; i < 100; i++)
        {
            NativeCopyEngine.SelfTestPanic().Should().Be(NativeCopyEngine.ErrPanic);
        }

        NativeMethods.fc_abi_version().Should().Be(NativeCopyEngine.ExpectedAbiVersion);
    }

    [Fact]
    public void Invalid_handles_and_arguments_are_rejected_not_crashing()
    {
        NativeMethods.fc_job_start(0).Should().Be(NativeCopyEngine.ErrInvalidHandle);
        NativeMethods.fc_job_cancel(ulong.MaxValue).Should().Be(NativeCopyEngine.ErrInvalidHandle);
        NativeMethods.fc_job_free(123456789).Should().Be(NativeCopyEngine.ErrInvalidHandle);
        unsafe
        {
            NativeMethods.fc_job_create(null, 0, null).Should().Be(NativeCopyEngine.ErrInvalidArg);
            NativeMethods.fc_job_poll(1, null).Should().Be(NativeCopyEngine.ErrInvalidArg);
        }

        FluentActions.Invoking(() => NativeCopyJob.Create("{ not json")).Should()
            .Throw<NativeCopyException>().Which.Code.Should().Be(NativeCopyEngine.ErrInvalidArg);

        var job = NativeCopyEngine.CreateJob(["x"], _dir);
        job.Dispose();
        FluentActions.Invoking(() => job.Poll()).Should().Throw<ObjectDisposedException>();
        job.Dispose(); // 두 번 해제해도 안전
    }

    [Fact]
    public void Missing_source_fails_the_job_only()
    {
        using var job = NativeCopyEngine.CreateJob([Path.Combine(_dir, "없는파일.bin")], Path.Combine(_dir, "out"));
        job.Start();
        var p = WaitDone(job);
        p.State.Should().Be(NativeJobState.Failed);
        p.LastError.Should().Be(NativeCopyEngine.ErrIo);
    }

    [Fact]
    public void Pause_resume_cancel_work_across_the_boundary()
    {
        var big = Path.Combine(_dir, "big.bin");
        using (var fs = File.Create(big))
        {
            fs.SetLength(256L * 1024 * 1024);
        }

        using var job = NativeCopyEngine.CreateJob([big], Path.Combine(_dir, "out"));
        job.Start();
        job.Pause();
        var paused = job.Poll();
        paused.State.Should().BeOneOf(NativeJobState.Paused, NativeJobState.Done);
        if (paused.State == NativeJobState.Paused)
        {
            Thread.Sleep(100);
            job.Poll().BytesDone.Should().BeLessThanOrEqualTo(paused.BytesDone + (8 << 20), "일시정지 중에는 최대 한 덩어리만 진행 (CopyFileEx 덩어리 크기)");
            job.Resume();
            job.Cancel();
            WaitDone(job).State.Should().Be(NativeJobState.Canceled);
        }
    }

    [Fact]
    public void Ten_thousand_create_poll_free_cycles_from_many_threads()
    {
        // 수명·동시성 스트레스: 해제된 핸들 재사용, 동시 poll이 메모리 오류 없이 오류 코드만 내는지.
        var errors = 0;
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1250; i++)
            {
                var job = NativeCopyEngine.CreateJob([Path.Combine(_dir, "none")], _dir);
                var h = job.Handle;
                job.Poll();
                job.Dispose();
                if (NativeMethods.fc_job_start(h) != NativeCopyEngine.ErrInvalidHandle)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });
        errors.Should().Be(0);
    }
}
