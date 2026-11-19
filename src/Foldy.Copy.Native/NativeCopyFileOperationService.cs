using Foldy.Presentation.Services;

namespace Foldy.Copy.Native;

/// <summary>사용자가 충돌 하나에 내린 결정. ApplyToAll이면 남은 충돌 전체에 같은 처리.</summary>
public readonly record struct ConflictDecision(ConflictAction Action, bool ApplyToAll = false);

public sealed class NativeCopyFileOperationOptions
{
    /// <summary>충돌 정책. Ask인데 <see cref="ResolveConflict"/>가 없으면 Rename으로 처리한다 (데이터를 잃지 않는 쪽).</summary>
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.Ask;

    public VerifyMode Verify { get; init; } = VerifyMode.SizeTime;

    /// <summary>0 = 자동 (설정 "복사 스레드: 자동(권장) / 수동 [4]").</summary>
    public uint Threads { get; init; }

    /// <summary>작업을 시작할 때마다 묻는 스레드 수 (설정 창에서 바꾸면 다음 복사부터 반영). 있으면 <see cref="Threads"/>보다 우선.</summary>
    public Func<int>? ThreadsProvider { get; init; }

    /// <summary>이번 작업에 쓸 스레드 수. 음수·0은 자동, 64 넘으면 64.</summary>
    public uint ResolveThreads() => ThreadsProvider is { } f ? (uint)Math.Clamp(f(), 0, 64) : Threads;

    /// <summary>작업 저널 폴더 (기본 %LOCALAPPDATA%\Foldy\jobs). null이면 저널 없음.</summary>
    public string? JournalDirectory { get; init; } = DefaultDataPath("jobs");

    /// <summary>드라이브 조합별 최적 스레드 수 (기본 %LOCALAPPDATA%\Foldy\copy_tuning.json).</summary>
    public string? TuningPath { get; init; } = DefaultDataPath("copy_tuning.json");

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>NeedsAttention일 때 충돌마다 불린다 (작업 패널 UI가 묻는 자리).</summary>
    public Func<CopyIssue, CancellationToken, Task<ConflictDecision>>? ResolveConflict { get; init; }

    private static string DefaultDataPath(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", name);
}

/// <summary>
/// 병렬 복사 엔진으로 복사·이동하는 <see cref="IFileOperationService"/>. 삭제·이름 바꾸기·새 폴더와
/// DLL을 쓸 수 없을 때의 복사·이동은 <paramref name="fallback"/>(IFileOperation)으로 넘긴다 (설계서 5.8 폴백).
/// </summary>
public sealed class NativeCopyFileOperationService(
    IFileOperationService fallback,
    ICopyEngine? engine = null,
    NativeCopyFileOperationOptions? options = null) : IFileOperationService
{
    private readonly ICopyEngine _engine = engine ?? NativeCopyEngineService.Shared;
    private readonly NativeCopyFileOperationOptions _options = options ?? new NativeCopyFileOperationOptions();

    public bool UsesNativeEngine => _engine.IsAvailable;

    public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        _engine.IsAvailable
            ? RunAsync(CopyMode.Copy, sources, destinationFolder, progress, ct)
            : fallback.CopyAsync(sources, destinationFolder, progress, ct);

    public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        _engine.IsAvailable
            ? RunAsync(CopyMode.Move, sources, destinationFolder, progress, ct)
            : fallback.MoveAsync(sources, destinationFolder, progress, ct);

    public Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        fallback.DeleteAsync(paths, recycle, progress, ct);

    public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) => fallback.RenameAsync(path, newName, ct);

    public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) => fallback.NewFolderAsync(parent, name, ct);

    private async Task<FileOpResult> RunAsync(CopyMode mode, IReadOnlyList<string> sources, string dest, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            return FileOpResult.Ok();
        }

        if (ct.IsCancellationRequested)
        {
            return FileOpResult.Canceled;
        }

        var resolver = _options.ResolveConflict;
        var policy = _options.Conflict == ConflictPolicy.Ask && resolver is null ? ConflictPolicy.Rename : _options.Conflict;
        var journal = _options.JournalDirectory is { } dir ? Path.Combine(dir, Guid.NewGuid().ToString("N") + ".journal") : null;
        var spec = new CopySpec
        {
            Sources = sources,
            DestDir = dest,
            Mode = mode,
            Conflict = policy,
            Verify = _options.Verify,
            Threads = _options.ResolveThreads(),
            JournalPath = journal,
            TuningPath = _options.TuningPath,
        };

        ICopyJob job;
        try
        {
            job = _engine.CreateJob(spec);
            job.Start();
        }
        catch (NativeCopyException ex)
        {
            return FileOpResult.Fail(ex.Message);
        }

        using (job)
        using (ct.Register(() => TryCancel(job)))
        {
            var asked = new HashSet<ulong>();
            while (true)
            {
                var p = job.Poll();
                progress?.Report(new FileOpProgress((long)p.BytesDone, (long)p.BytesTotal, SafeName(job)));
                if (p.IsTerminal)
                {
                    return Finish(p, job, journal);
                }

                if (p.State == NativeJobState.NeedsAttention && resolver is not null && !ct.IsCancellationRequested)
                {
                    await AskAsync(job, resolver, asked, ct);
                }

                // 취소 후에도 엔진이 Canceled가 될 때까지 기다린다 (워커가 대상 정리를 끝내도록).
                await Task.Delay(_options.PollInterval, CancellationToken.None);
            }
        }
    }

    private static async Task AskAsync(ICopyJob job, Func<CopyIssue, CancellationToken, Task<ConflictDecision>> resolver, HashSet<ulong> asked, CancellationToken ct)
    {
        foreach (var issue in job.GetIssues())
        {
            if (issue.Kind != CopyIssueKind.Conflict || issue.Resolved || !asked.Add(issue.Id))
            {
                continue;
            }

            ConflictDecision d;
            try
            {
                d = await resolver(issue, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (job.Resolve(issue.Id, d.Action, d.ApplyToAll) && d.ApplyToAll)
            {
                return;
            }
        }
    }

    private static FileOpResult Finish(NativeProgress p, ICopyJob job, string? journal)
    {
        switch (p.State)
        {
            case NativeJobState.Done when p.FilesFailed == 0:
                return FileOpResult.Ok();
            case NativeJobState.Done:
                var first = job.GetIssues().FirstOrDefault(i => i.Kind == CopyIssueKind.Failed);
                return FileOpResult.Fail($"파일 {p.FilesFailed}개를 처리하지 못했습니다" + (first is null ? "" : $": {first.Source} — {first.Message}"));
            case NativeJobState.Canceled:
                // 사용자가 취소한 작업은 이어 하지 않으므로 저널을 지운다.
                TryDelete(journal);
                return FileOpResult.Canceled;
            default:
                return FileOpResult.Fail($"복사 엔진 오류 (코드 {p.LastError})");
        }
    }

    private static string? SafeName(ICopyJob job)
    {
        try
        {
            var n = job.CurrentName();
            return n.Length == 0 ? null : n;
        }
        catch (NativeCopyException)
        {
            return null;
        }
    }

    private static void TryCancel(ICopyJob job)
    {
        try
        {
            job.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (NativeCopyException)
        {
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
