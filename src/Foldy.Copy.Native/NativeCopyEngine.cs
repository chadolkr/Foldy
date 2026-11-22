using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Foldy.Copy.Native;

public enum NativeJobState : uint { Created = 0, Running = 1, Paused = 2, Done = 3, Failed = 4, Canceled = 5, NeedsAttention = 6 }

/// <summary>엔진 오류 코드 중 화면이 따로 다루는 것 (docs/copy-engine.md).</summary>
public static class NativeCopyErrors
{
    /// <summary>시작 전에 본 대상 드라이브 빈 공간이 모자람. 아무것도 쓰지 않았고 BytesTotal = 필요한 바이트.</summary>
    public const int NoSpace = -9;

    /// <summary>대상이 FAT32라 4GB가 넘는 파일을 담을 수 없음. 아무것도 쓰지 않았고 BytesTotal = 그 파일 크기.</summary>
    public const int FileTooBig = -10;
}

/// <summary>엔진 파이프라인 단계 (스캐너 → 계획기 → 워커 → 마무리).</summary>
public enum CopyPhase : uint { Pending = 0, Scanning = 1, Planning = 2, Copying = 3, Finalizing = 4, Finished = 5 }

public enum CopyMode { Copy, Move }

/// <summary>대상에 같은 이름이 있을 때. Ask는 작업이 NeedsAttention이 되고 <see cref="NativeCopyJob.Resolve"/>를 기다린다.</summary>
public enum ConflictPolicy { Skip, Overwrite, Rename, Ask }

public enum VerifyMode { None, SizeTime, Hash }

/// <summary>충돌 해결 방법 (fc_job_resolve의 action 값과 같다).</summary>
public enum ConflictAction { Skip = 0, Overwrite = 1, Rename = 2 }

public enum CopyIssueKind { Conflict, Failed }

/// <summary>진행 상황 스냅샷 (FcProgress). 앞의 여섯 값은 ABI v1과 같다.</summary>
public readonly record struct NativeProgress(
    NativeJobState State,
    int LastError,
    ulong FilesDone,
    ulong FilesTotal,
    ulong BytesDone,
    ulong BytesTotal,
    ulong BytesPerSecond = 0,
    ulong FilesFailed = 0,
    ulong FilesSkipped = 0,
    uint IssuesPending = 0,
    uint ThreadsActive = 0,
    uint ThreadLimit = 0,
    CopyPhase Phase = CopyPhase.Pending)
{
    public bool IsTerminal => State is NativeJobState.Done or NativeJobState.Failed or NativeJobState.Canceled;
}

/// <summary>작업 패널의 문제 목록 항목 (충돌 또는 실패).</summary>
public sealed record CopyIssue(ulong Id, CopyIssueKind Kind, string Source, string Dest, string Message, bool Resolved);

/// <summary>fc_job_create에 넘기는 작업 명세 (docs/copy-engine.md). null 값은 엔진 기본값을 쓴다.</summary>
public sealed record CopySpec
{
    [JsonPropertyName("sources")]
    public required IReadOnlyList<string> Sources { get; init; }

    [JsonPropertyName("dest_dir")]
    public required string DestDir { get; init; }

    [JsonPropertyName("mode")]
    public CopyMode Mode { get; init; } = CopyMode.Copy;

    [JsonPropertyName("conflict")]
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Ask;

    [JsonPropertyName("verify")]
    public VerifyMode Verify { get; init; } = VerifyMode.SizeTime;

    /// <summary>0 = 자동 조절 (SSD 4..16, HDD 1..2, 네트워크 8..32).</summary>
    [JsonPropertyName("threads")]
    public uint Threads { get; init; }

    /// <summary>작업 저널 (jobs/&lt;id&gt;.journal). 성공하면 엔진이 지운다.</summary>
    [JsonPropertyName("journal_path")]
    public string? JournalPath { get; init; }

    [JsonPropertyName("resume")]
    public bool Resume { get; init; }

    /// <summary>드라이브 조합별 최적 스레드 수 저장 파일.</summary>
    [JsonPropertyName("tuning_path")]
    public string? TuningPath { get; init; }

    [JsonPropertyName("large_threshold")]
    public ulong? LargeThreshold { get; init; }

    [JsonPropertyName("tune_interval_ms")]
    public ulong? TuneIntervalMs { get; init; }

    /// <summary>같은 볼륨 이동도 이름 변경 대신 복사 → 검증 → 원본 삭제로 처리 (진단용).</summary>
    [JsonPropertyName("move_by_copy")]
    public bool MoveByCopy { get; init; }

    /// <summary>시험용: 파일 n개 뒤 크래시처럼 멈춤.</summary>
    [JsonInclude]
    [JsonPropertyName("_test_abort_after_files")]
    internal ulong? TestAbortAfterFiles { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, NativeCopyEngine.Json);
}

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
    public const uint ExpectedAbiVersion = 2;

    public const int Ok = 0;
    public const int ErrInvalidArg = -1;
    public const int ErrInvalidHandle = -2;
    public const int ErrPanic = -3;
    public const int ErrInvalidState = -4;
    public const int ErrIo = -5;
    public const int ErrBufferTooSmall = -6;
    public const int ErrNotFound = -7;
    public const int ErrAborted = -8;

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

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

    /// <summary>ABI v1 호환: 기본 정책(충돌은 Ask, 크기·시간 검증)으로 복사.</summary>
    public static NativeCopyJob CreateJob(IReadOnlyList<string> sources, string destDir) =>
        CreateJob(new CopySpec { Sources = sources, DestDir = destDir });

    public static NativeCopyJob CreateJob(CopySpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return NativeCopyJob.Create(spec.ToJson());
    }

    /// <summary>저널 파일에 기록된 작업 명세(JSON)를 읽는다.</summary>
    public static unsafe string ReadJournalSpec(string journalPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(journalPath);
        fixed (char* p = journalPath)
        {
            var path = (ushort*)p;
            var len = (nuint)journalPath.Length;
            return Utf16.Read((buf, cap, outLen) => NativeMethods.fc_journal_read_spec(path, len, buf, cap, outLen), "journal_read_spec");
        }
    }

    /// <summary>크래시·재부팅 뒤 저널로 이어서 복사하는 작업을 만든다 (끝난 파일은 크기+시간이 같으면 건너뜀).</summary>
    public static NativeCopyJob ResumeFromJournal(string journalPath)
    {
        var node = JsonNode.Parse(ReadJournalSpec(journalPath))?.AsObject()
            ?? throw new NativeCopyException(ErrInvalidArg, "journal_read_spec");
        node["resume"] = true;
        node["journal_path"] = journalPath;
        return NativeCopyJob.Create(node.ToJsonString());
    }

    /// <summary>시험용: 엔진 안의 panic이 프로세스를 죽이지 않고 오류 코드로 돌아오는지 확인.</summary>
    internal static int SelfTestPanic() => NativeMethods.fc_selftest_panic();
}

/// <summary>호출자 버퍼에 UTF-16을 받는 함수 공통 처리 (작으면 필요한 길이로 한 번 더).</summary>
internal static unsafe class Utf16
{
    public delegate int Getter(ushort* buf, nuint cap, nuint* outLen);

    public static string Read(Getter get, string op)
    {
        const int Initial = 512;
        var stack = stackalloc char[Initial];
        nuint len;
        var rc = get((ushort*)stack, Initial, &len);
        if (rc == NativeCopyEngine.Ok)
        {
            return new string(stack, 0, (int)len);
        }

        while (rc == NativeCopyEngine.ErrBufferTooSmall)
        {
            var heap = new char[(int)len];
            fixed (char* h = heap)
            {
                rc = get((ushort*)h, (nuint)heap.Length, &len);
            }

            if (rc == NativeCopyEngine.Ok)
            {
                return new string(heap, 0, (int)len);
            }
        }

        throw new NativeCopyException(rc, op);
    }
}

/// <summary>네이티브 작업 핸들. Dispose하면 엔진에서 해제된다(실행 중이면 취소).</summary>
public sealed class NativeCopyJob : ICopyJob
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
            (NativeJobState)p.state, p.last_error, p.files_done, p.files_total, p.bytes_done, p.bytes_total,
            p.bytes_per_sec, p.files_failed, p.files_skipped, p.issues_pending, p.threads_active, p.thread_limit,
            (CopyPhase)p.phase);
    }

    /// <summary>지금(마지막으로 시작한) 파일의 원본 전체 경로. 아직 없으면 빈 문자열.</summary>
    public unsafe string CurrentName()
    {
        var h = Live();
        return Utf16.Read((buf, cap, len) => NativeMethods.fc_job_current_name(h, buf, cap, len), "current_name");
    }

    public unsafe IReadOnlyList<CopyIssue> GetIssues()
    {
        var h = Live();
        var json = Utf16.Read((buf, cap, len) => NativeMethods.fc_job_issues(h, buf, cap, len), "issues");
        return json.Length == 0 ? [] : JsonSerializer.Deserialize<List<CopyIssue>>(json, NativeCopyEngine.Json) ?? [];
    }

    /// <summary>충돌 해결. 이미 해결됐거나 없는 항목이면 false.</summary>
    public bool Resolve(ulong issueId, ConflictAction action, bool applyToAll = false)
    {
        var rc = NativeMethods.fc_job_resolve(Live(), issueId, (uint)action, applyToAll ? 1u : 0u);
        if (rc == NativeCopyEngine.ErrNotFound)
        {
            return false;
        }

        Check(rc, "resolve");
        return true;
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
