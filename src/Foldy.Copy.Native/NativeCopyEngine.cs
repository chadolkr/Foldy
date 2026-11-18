using System.Text.Json;

namespace Foldy.Copy.Native;

public enum NativeJobState : uint { Created = 0, Running = 1, Paused = 2, Done = 3, Failed = 4, Canceled = 5 }

public readonly record struct NativeProgress(
    NativeJobState State, int LastError, ulong FilesDone, ulong FilesTotal, ulong BytesDone, ulong BytesTotal);

public sealed class NativeCopyException(int code, string op)
    : Exception($"foldy_copy {op} 실패 (코드 {code})")
{
    public int Code { get; } = code;
}

/// <summary>
/// foldy_copy.dll 로드 상태 확인과 작업 생성. 로드에 실패하거나 ABI 버전이 다르면
/// <see cref="IsAvailable"/>가 false이고, 호출자는 IFileOperation 경로로 폴백한다 (설계서 5.8).
/// </summary>
public static class NativeCopyEngine
{
    public const uint ExpectedAbiVersion = 1;

    public const int Ok = 0;
    public const int ErrInvalidArg = -1;
    public const int ErrInvalidHandle = -2;
    public const int ErrPanic = -3;
    public const int ErrInvalidState = -4;
    public const int ErrIo = -5;

    private static readonly Lazy<(bool Ok, string? Error)> Probe = new(() =>
    {
        try
        {
            var v = NativeMethods.fc_abi_version();
            return v == ExpectedAbiVersion ? (true, null) : (false, $"ABI 버전 불일치: {v}");
        }
        catch (DllNotFoundException ex)
        {
            return (false, ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return (false, ex.Message);
        }
        catch (BadImageFormatException ex)
        {
            return (false, ex.Message);
        }
    });

    public static bool IsAvailable => Probe.Value.Ok;

    public static string? UnavailableReason => Probe.Value.Error;

    public static NativeCopyJob CreateJob(IReadOnlyList<string> sources, string destDir)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sources"] = sources,
            ["dest_dir"] = destDir,
        });
        return NativeCopyJob.Create(json);
    }

    /// <summary>시험용: 엔진 안의 panic이 프로세스를 죽이지 않고 오류 코드로 돌아오는지 확인.</summary>
    internal static int SelfTestPanic() => NativeMethods.fc_selftest_panic();
}

/// <summary>네이티브 작업 핸들. Dispose하면 엔진에서 해제된다(실행 중이면 취소).</summary>
public sealed class NativeCopyJob : IDisposable
{
    private ulong _handle;

    private NativeCopyJob(ulong handle) => _handle = handle;

    internal ulong Handle => _handle;

    internal static unsafe NativeCopyJob Create(string specJson)
    {
        ulong h;
        int rc;
        fixed (char* p = specJson)
        {
            rc = NativeMethods.fc_job_create((ushort*)p, (nuint)specJson.Length, &h);
        }

        Check(rc, "create");
        return new NativeCopyJob(h);
    }

    public void Start() => Check(NativeMethods.fc_job_start(Live()), "start");

    public void Pause() => Check(NativeMethods.fc_job_pause(Live()), "pause");

    public void Resume() => Check(NativeMethods.fc_job_resume(Live()), "resume");

    public void Cancel() => Check(NativeMethods.fc_job_cancel(Live()), "cancel");

    public unsafe NativeProgress Poll()
    {
        FcProgress p;
        Check(NativeMethods.fc_job_poll(Live(), &p), "poll");
        return new NativeProgress(
            (NativeJobState)p.state, p.last_error, p.files_done, p.files_total, p.bytes_done, p.bytes_total);
    }

    private ulong Live()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        return _handle;
    }

    private static void Check(int rc, string op)
    {
        if (rc != NativeCopyEngine.Ok)
        {
            throw new NativeCopyException(rc, op);
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            _ = NativeMethods.fc_job_free(h);
        }
    }
}
