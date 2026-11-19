namespace Foldy.Git;

/// <summary>
/// Git 기능의 단일 진입점 (설계서 5.7). 읽기는 파서, 쓰기는 git.exe CLI.
/// 모든 메서드는 예외 대신 <see cref="GitResult"/> 로 실패를 돌려주며, 취소는 GitFailure.Cancelled 이다.
/// repo 인수는 저장소 워킹트리 루트 경로.
/// </summary>
public interface IGitService
{
    /// <summary>git.exe 사용 가능 여부 (없으면 쓰기 버튼에 설치 안내).</summary>
    bool IsGitAvailable { get; }

    /// <summary>folder 를 포함하는 저장소 감지 (캐시됨). 없으면 null.</summary>
    GitRepoInfo? FindRepo(string folder);

    // ── 읽기 ──
    Task<GitResult<RepoStatus>> GetStatusAsync(string repo, bool includeIgnored = false, CancellationToken ct = default);
    IAsyncEnumerable<CommitNode> GetLogAsync(string repo, LogFilter filter, CancellationToken ct = default);
    Task<GitResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(string repo, CancellationToken ct = default);
    Task<GitResult<IReadOnlyList<GitStash>>> GetStashesAsync(string repo, CancellationToken ct = default);

    /// <summary>파일 하나의 diff (unified). 이진 파일이면 IsBinary, maxLines 를 넘으면 Truncated.</summary>
    Task<GitResult<FileDiff>> GetDiffAsync(string repo, GitDiffTarget target, int maxLines = GitDiffParser.DefaultMaxLines, CancellationToken ct = default);

    /// <summary>파일 blame (git blame --porcelain). revision 이 null 이면 워킹트리 기준.</summary>
    Task<GitResult<IReadOnlyList<BlameLine>>> GetBlameAsync(string repo, string path, string? revision = null, CancellationToken ct = default);

    // ── 쓰기 (저장소당 한 번에 하나) ──
    Task<GitResult> StageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<GitResult> UnstageAsync(string repo, IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<GitResult> CommitAsync(string repo, string message, bool amend = false, CancellationToken ct = default);

    Task<GitResult> FetchAsync(string repo, IProgress<GitProgress>? progress = null, bool interactive = false, CancellationToken ct = default);
    Task<GitResult> PullAsync(string repo, PullMode mode, IProgress<GitProgress>? progress = null, CancellationToken ct = default);
    /// <summary>force 는 --force-with-lease 만 지원한다 (--force 는 제공하지 않음).</summary>
    Task<GitResult> PushAsync(string repo, bool setUpstream, bool forceWithLease, IProgress<GitProgress>? progress = null, CancellationToken ct = default);

    Task<GitResult> StashPushAsync(string repo, string? message = null, bool includeUntracked = true, CancellationToken ct = default);
    Task<GitResult> StashPopAsync(string repo, string? stashName = null, CancellationToken ct = default);

    Task<GitResult> CreateBranchAsync(string repo, string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default);
    Task<GitResult> CheckoutAsync(string repo, string branch, CancellationToken ct = default);

    /// <summary>git init: 폴더를 새 저장소로 만든다.</summary>
    Task<GitResult> InitAsync(string folder, CancellationToken ct = default);

    /// <summary>dubious ownership 시 [이 저장소 신뢰하기]: 전역 safe.directory 에 경로를 추가한다.</summary>
    Task<GitResult> AddSafeDirectoryAsync(string path, CancellationToken ct = default);
}
