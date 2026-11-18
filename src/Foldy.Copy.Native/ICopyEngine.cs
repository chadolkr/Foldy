namespace Foldy.Copy.Native;

/// <summary>
/// 복사 엔진 추상화 (설계서 5.8 격리 옵션). 지금은 프로세스 안 cdylib를 쓰지만, 크래시가 반복되면
/// 같은 C ABI를 foldy-copy-host.exe로 감싸 파이프로 호출하는 구현으로 바꿀 수 있게 이 인터페이스로만 쓴다.
/// </summary>
public interface ICopyEngine
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    ICopyJob CreateJob(CopySpec spec);

    ICopyJob ResumeFromJournal(string journalPath);
}

/// <summary>실행 중인 복사 작업 하나. 진행률은 <see cref="Poll"/>로 초당 10회쯤 가져간다 (콜백 없음).</summary>
public interface ICopyJob : IDisposable
{
    void Start();

    void Pause();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "설계서 5.5 IJob의 Pause/Resume 이름을 따른다")]
    void Resume();

    void Cancel();

    NativeProgress Poll();

    string CurrentName();

    IReadOnlyList<CopyIssue> GetIssues();

    bool Resolve(ulong issueId, ConflictAction action, bool applyToAll = false);
}

/// <summary>foldy_copy.dll을 쓰는 기본 구현.</summary>
public sealed class NativeCopyEngineService : ICopyEngine
{
    public static NativeCopyEngineService Shared { get; } = new();

    public bool IsAvailable => NativeCopyEngine.IsAvailable;

    public string? UnavailableReason => NativeCopyEngine.UnavailableReason;

    public ICopyJob CreateJob(CopySpec spec) => NativeCopyEngine.CreateJob(spec);

    public ICopyJob ResumeFromJournal(string journalPath) => NativeCopyEngine.ResumeFromJournal(journalPath);
}
