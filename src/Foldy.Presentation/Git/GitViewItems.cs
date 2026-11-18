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
