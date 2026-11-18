using Foldy.Core.FileSystem;

namespace Foldy.Git;

/// <summary>파일 하나의 변경 종류 (porcelain v2 의 XY 한 글자에 대응).</summary>
public enum GitChange : byte
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
    Ignored,
}

/// <summary>status 한 줄. Staged = 인덱스(X), Unstaged = 워킹트리(Y).</summary>
/// <param name="Path">저장소 루트 기준 상대 경로 ('/' 구분). 미추적 폴더는 끝에 '/'.</param>
/// <param name="OrigPath">이름 변경·복사의 원본 경로.</param>
public sealed record GitFileEntry(string Path, string? OrigPath, GitChange Staged, GitChange Unstaged)
{
    public bool IsConflicted => Staged == GitChange.Conflicted || Unstaged == GitChange.Conflicted;
    public bool IsUntracked => Unstaged == GitChange.Untracked;
    public bool IsIgnored => Unstaged == GitChange.Ignored;

    /// <summary>목록 배지용 요약 상태 (Foldy.Core 의 GitFileStatus).</summary>
    public GitFileStatus ToBadge()
    {
        if (IsConflicted) return GitFileStatus.Conflicted;
        if (IsUntracked) return GitFileStatus.Untracked;
        if (IsIgnored) return GitFileStatus.Ignored;
        // 워킹트리에 변경이 있으면 수정, 인덱스에만 추가/이름변경이 있으면 추가
        if (Unstaged != GitChange.None) return GitFileStatus.Modified;
        return Staged is GitChange.Added or GitChange.Renamed or GitChange.Copied
            ? GitFileStatus.Added
            : GitFileStatus.Modified;
    }
}

/// <summary>status --branch 결과.</summary>
public sealed record RepoStatus(
    string? BranchName,
    string? HeadOid,
    bool IsDetached,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileEntry> Files)
{
    public bool HasUpstream => Upstream is not null;
    public bool HasConflicts => Files.Any(f => f.IsConflicted);
    /// <summary>무시된 파일을 제외한 변경 개수 (상태바 표시용).</summary>
    public int ChangeCount => Files.Count(f => !f.IsIgnored);
}

public enum GitRefKind : byte { LocalBranch, RemoteBranch, Tag, Head }

/// <summary>커밋에 달린 ref 장식 (%D).</summary>
public sealed record GitRef(string Name, GitRefKind Kind, bool IsCurrentHead = false);

/// <summary>커밋 그래프의 노드 하나.</summary>
public sealed record CommitNode(
    string Hash,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset Date,
    string Subject,
    IReadOnlyList<GitRef> Refs)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
    public bool IsMerge => Parents.Count > 1;
}

/// <summary>log 조회 조건. Skip/Count 로 페이징한다.</summary>
public sealed record LogFilter
{
    public int Skip { get; init; }
    public int Count { get; init; } = 1000;
    /// <summary>true 면 모든 ref (--all), false 면 HEAD 또는 Revision.</summary>
    public bool AllRefs { get; init; } = true;
    public string? Revision { get; init; }
    public string? Author { get; init; }
    /// <summary>커밋 메시지 검색 (대소문자 무시).</summary>
    public string? MessageContains { get; init; }
    /// <summary>특정 파일/폴더의 히스토리 (저장소 상대 경로).</summary>
    public string? Path { get; init; }
}

public sealed record GitBranch(string Name, bool IsCurrent, bool IsRemote, string? Upstream, int Ahead, int Behind, string Oid);

public sealed record GitStash(string Name, string Hash, DateTimeOffset Date, string Message);

public enum PullMode : byte { Merge, Rebase }

/// <summary>--progress 로 파싱한 진행 상황. Percent 가 없는 줄은 보고하지 않는다.</summary>
public readonly record struct GitProgress(string Stage, int Percent, string Raw);

public enum GitFailure : byte
{
    None,
    GitNotFound,
    DubiousOwnership,
    AuthenticationFailed,
    MergeConflict,
    NothingToCommit,
    NotARepository,
    Cancelled,
    Other,
}

/// <summary>git 호출 결과. 예외 대신 유형화된 실패로 돌려준다.</summary>
public sealed record GitResult(bool Success, GitFailure Failure, int ExitCode, string Output, string Error)
{
    /// <summary>DubiousOwnership 일 때 git 이 안내한 저장소 경로.</summary>
    public string? SafeDirectoryCandidate { get; init; }

    public static GitResult Ok(string output = "") => new(true, GitFailure.None, 0, output, "");
    public static GitResult Fail(GitFailure failure, string error = "", int exitCode = -1) => new(false, failure, exitCode, "", error);
}

public sealed record GitResult<T>(GitResult Result, T? Value)
{
    public bool Success => Result.Success;
}

/// <summary>저장소 감지 결과.</summary>
/// <param name="Root">워킹트리 루트 (.git 이 있는 폴더).</param>
/// <param name="GitDir">실제 git 디렉터리 (.git 이 파일이면 gitdir: 이 가리키는 곳).</param>
/// <param name="IsLinkedGitDir">.git 이 파일 (워크트리/서브모듈).</param>
public sealed record GitRepoInfo(string Root, string GitDir, bool IsLinkedGitDir);
