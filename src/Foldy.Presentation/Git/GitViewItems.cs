using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>Local Changes 목록의 파일 한 줄. Unstaged / Staged 목록이 각각 자기 항목을 가진다.</summary>
/// <param name="Path">저장소 루트 기준 상대 경로 ('/' 구분, 미추적 폴더는 끝에 '/').</param>
public sealed record GitChangeItem(string Path, string? OrigPath, GitChange Change, bool IsStaged)
{
    /// <summary>목록 배지 글자 (M A D R C T ? !).</summary>
    public string Letter => Change switch
    {
        GitChange.Modified => "M",
        GitChange.Added => "A",
        GitChange.Deleted => "D",
        GitChange.Renamed => "R",
        GitChange.Copied => "C",
        GitChange.TypeChanged => "T",
        GitChange.Untracked => "?",
        GitChange.Conflicted => "!",
        GitChange.Ignored => "I",
        _ => "",
    };

    /// <summary>목록 배지 글자 (한국어): 수정 · 추가 · 삭제 · 새 파일 · 충돌 …</summary>
    public string Label => Change switch
    {
        GitChange.Modified => "수정",
        GitChange.Added => "추가",
        GitChange.Deleted => "삭제",
        GitChange.Renamed => "이름 변경",
        GitChange.Copied => "복사",
        GitChange.TypeChanged => "형식 변경",
        GitChange.Untracked => "새 파일",
        GitChange.Conflicted => "충돌",
        _ => "",
    };

    public string FileName
    {
        get
        {
            var p = Path.TrimEnd('/');
            return p[(p.LastIndexOf('/') + 1)..] + (Path.EndsWith('/') ? "/" : "");
        }
    }

    public string Folder
    {
        get
        {
            var p = Path.TrimEnd('/');
            var i = p.LastIndexOf('/');
            return i > 0 ? p[..i] : "";
        }
    }
}

/// <summary>변경 사항 목록의 한 줄: 그룹 머리글(스테이지됨/변경됨/새 파일) 또는 파일.</summary>
public sealed record GitChangeRow(string? HeaderText, int Count, GitChangeItem? Item)
{
    public bool IsHeader => Item is null;

    /// <summary>체크 상태 = 스테이지됨.</summary>
    public bool IsChecked => Item?.IsStaged ?? false;

    public string HeaderLabel => HeaderText is null ? "" : $"{HeaderText} ({Count})";

    public static GitChangeRow Header(string text, int count) => new(text, count, null);

    public static GitChangeRow File(GitChangeItem item) => new(null, 0, item);
}

/// <summary>그래프 목록의 한 줄: 커밋 + 레인 그리기 정보.</summary>
public sealed record GitCommitRow(CommitNode Node, GraphRow Graph)
{
    /// <summary>현지 시각 "yyyy-MM-dd HH:mm".</summary>
    public string DateText => Node.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>사이드바·브랜치 메뉴의 브랜치 한 줄.</summary>
public sealed record GitBranchItem(string Name, bool IsCurrent, bool IsRemote, int Ahead, int Behind)
{
    public string Track => (Ahead > 0 ? $"↑{Ahead} " : "") + (Behind > 0 ? $"↓{Behind}" : "");
}

public sealed record GitStashItem(string Name, string Message, DateTimeOffset Date);
